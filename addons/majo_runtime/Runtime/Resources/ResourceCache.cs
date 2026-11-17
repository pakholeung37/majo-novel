#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Resources;

// Serializes cache retention and shared provider loads under one gate.
internal sealed class ResourceCache
{
    private readonly object gate = new();
    private readonly IRuntimeLifetime lifetime;
    private readonly IRuntimeLogger logger;
    private readonly Dictionary<CacheKey, CacheEntry> loaded = new();
    private readonly Dictionary<CacheKey, PendingLoad> pending = new();
    private readonly HashSet<IResourceLease> activeLeases = new();
    private readonly long budgetBytes;
    private readonly bool releaseOnLastLease;
    private CancellationTokenSource? loadLifetime;
    private long cacheHits;
    private long cacheMisses;
    private long evictionCount;
    private long reportedPressureBytes;

    public ResourceCache(IRuntimeLifetime lifetime, IRuntimeLogger logger, long budgetBytes, bool releaseOnLastLease)
    {
        this.lifetime = lifetime;
        this.logger = logger;
        this.budgetBytes = budgetBytes;
        this.releaseOnLastLease = releaseOnLastLease;
    }

    public int LoadedResourceCount { get { lock (gate) return loaded.Count; } }

    public bool RemoveResource(string providerId, ResourceKey key, Func<bool> unregister)
    {
        lock (gate)
        {
            var keys = loaded.Keys.Where(value => value.ProviderId == providerId && value.ResolvedPath == key.Path).ToArray();
            if (pending.Keys.Any(value => value.ProviderId == providerId && value.ResolvedPath == key.Path) ||
                keys.Any(value => loaded[value].LeaseCount > 0))
                throw new MajoResourceException($"Cannot unregister leased or loading resource '{key}'.");
            foreach (var cacheKey in keys) loaded.Remove(cacheKey);
            return unregister();
        }
    }

    public bool IsPinned(ResourceKey key, Type resourceType, ResolvedResource resolved)
    {
        if (resolved.Location is null) return false;
        var cacheKey = RequireCacheKey(key, resourceType, resolved);
        lock (gate) return loaded.TryGetValue(cacheKey, out var entry) && entry.PinnedLeaseCount > 0;
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        CancelLoadLifetime();
        ReleaseAllLeases();
        await AwaitPendingAsync(cancellationToken);
        lock (gate) loaded.Clear();
    }

    public void TrackLease(IResourceLease lease) { lock (gate) activeLeases.Add(lease); }
    public void ForgetLease(IResourceLease lease) { lock (gate) activeLeases.Remove(lease); }

    public TResource Load<TResource>(ResourceKey key, ResolvedResource resolved, long estimatedBytes)
        where TResource : class
    {
        var cacheKey = RequireCacheKey(key, typeof(TResource), resolved);
        lock (gate)
        {
            if (loaded.TryGetValue(cacheKey, out var cached))
            {
                cacheHits++;
                cached.LastAccessedAt = DateTimeOffset.UtcNow;
                LogCacheHit(key);
                return Cast<TResource>(cached.Resource, resolved.Location!.Path);
            }
            if (pending.ContainsKey(cacheKey))
                throw new MajoResourceException(
                    $"Resource '{resolved.Location!.Path}' is loading asynchronously; await the existing load.");
            try
            {
                cacheMisses++;
                LogCacheMiss(key, resolved.Provider.Id);
                var resource = RequireType(
                    resolved.Provider.Load(resolved.Location!, typeof(TResource)),
                    typeof(TResource), resolved.Location!.Path);
                loaded.Add(cacheKey, new CacheEntry(resource, estimatedBytes));
                EvictToBudgetLocked(cacheKey, "budget_after_sync_load");
                logger.Debug("resource", "resource.load_completed",
                    $"Resource '{key.Path}' loaded with provider '{resolved.Provider.Id}'.");
                return Cast<TResource>(resource, resolved.Location.Path);
            }
            catch (Exception exception)
            {
                logger.Error("resource", "resource.load_failed",
                    $"Resource '{key.Path}' failed to load with provider '{resolved.Provider.Id}'.", exception);
                throw;
            }
        }
    }

    public Task<TResource> LoadAsync<TResource>(
        ResourceKey key,
        ResolvedResource resolved,
        long estimatedBytes,
        CancellationToken cancellationToken = default)
        where TResource : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = RequireCacheKey(key, typeof(TResource), resolved);
        var loading = GetOrStartLoad(key, typeof(TResource), resolved, cacheKey, estimatedBytes);
        return AwaitTypedAsync<TResource>(loading, resolved.Location!.Path, cancellationToken);
    }

    public int TrimCache()
    {
        lock (gate) return EvictToBudgetLocked(null, "explicit_trim", true);
    }

    public IReadOnlyList<ResourceDiagnosticEntry> CaptureDiagnosticResources()
    {
        lock (gate)
            return loaded.Values.Select(entry => new ResourceDiagnosticEntry(
                entry.Resource, entry.LeaseCount, entry.PinnedLeaseCount)).ToArray();
    }

    public ResourceCacheSnapshot CaptureCacheSnapshot()
    {
        lock (gate)
        {
            var entries = loaded.Select(pair => new ResourceCacheEntrySnapshot(
                    pair.Key.ResourceType.FullName ?? pair.Key.ResourceType.Name,
                    pair.Key.ProviderId,
                    pair.Key.ResolvedPath,
                    pair.Value.EstimatedBytes,
                    pair.Value.LeaseCount,
                    pair.Value.PinnedLeaseCount,
                    pair.Value.LastAccessedAt))
                .OrderBy(entry => entry.ResolvedPath, StringComparer.Ordinal).ToArray();
            return new ResourceCacheSnapshot(
                loaded.Count, pending.Count, activeLeases.Count,
                entries.Sum(entry => entry.EstimatedBytes), budgetBytes,
                cacheHits, cacheMisses, evictionCount, entries);
        }
    }

    public async Task<ResourceLeaseAcquisition<TResource>> AcquireForLeaseAsync<TResource>(
        ResourceKey key,
        ResolvedResource resolved,
        long estimatedBytes,
        bool pinned,
        CancellationToken cancellationToken)
        where TResource : class
    {
        var cacheKey = RequireCacheKey(key, typeof(TResource), resolved);
        var loading = GetOrStartLoad(key, typeof(TResource), resolved, cacheKey, estimatedBytes);
        var resource = Cast<TResource>(await loading.WaitAsync(cancellationToken), resolved.Location!.Path);
        CacheEntry acquiredEntry;
        lock (gate)
        {
            // Reset releases loading leases before clearing this cache. Check
            // cancellation in the same critical section that publishes retention.
            cancellationToken.ThrowIfCancellationRequested();
            if (!loaded.TryGetValue(cacheKey, out var entry))
            {
                entry = new CacheEntry(resource, estimatedBytes);
                loaded.Add(cacheKey, entry);
            }
            // The completed load may have been evicted and replaced while this
            // continuation was queued. Return the instance whose retention we own.
            resource = Cast<TResource>(entry.Resource, resolved.Location.Path);
            entry.EstimatedBytes = Math.Max(entry.EstimatedBytes, estimatedBytes);
            entry.LeaseCount++;
            if (pinned) entry.PinnedLeaseCount++;
            entry.LastAccessedAt = DateTimeOffset.UtcNow;
            EvictToBudgetLocked(cacheKey, "budget_after_lease");
            acquiredEntry = entry;
        }
        return new ResourceLeaseAcquisition<TResource>(resource, resolved.Location.Path,
            () => ReleaseLease(cacheKey, acquiredEntry, pinned));
    }

    private void ReleaseLease(CacheKey cacheKey, CacheEntry acquiredEntry, bool pinned)
    {
        lock (gate)
        {
            if (loaded.TryGetValue(cacheKey, out var entry) && ReferenceEquals(entry, acquiredEntry))
            {
                entry.LeaseCount = Math.Max(0, entry.LeaseCount - 1);
                if (pinned) entry.PinnedLeaseCount = Math.Max(0, entry.PinnedLeaseCount - 1);
                entry.LastAccessedAt = DateTimeOffset.UtcNow;
                if (releaseOnLastLease && entry.LeaseCount == 0 && entry.PinnedLeaseCount == 0)
                {
                    loaded.Remove(cacheKey);
                    evictionCount++;
                    logger.Info("resource", "resource.cache_evicted",
                        $"Evicted Runtime cache entry '{cacheKey.ResolvedPath}' (last_lease_released).");
                }
            }
            EvictToBudgetLocked(null, "budget_after_release");
        }
    }

    private Task<object> GetOrStartLoad(
        ResourceKey logicalKey,
        Type resourceType,
        ResolvedResource resolved,
        CacheKey cacheKey,
        long estimatedBytes)
    {
        lock (gate)
        {
            if (loaded.TryGetValue(cacheKey, out var cached))
            {
                cacheHits++;
                cached.EstimatedBytes = Math.Max(cached.EstimatedBytes, estimatedBytes);
                cached.LastAccessedAt = DateTimeOffset.UtcNow;
                LogCacheHit(logicalKey);
                return Task.FromResult(cached.Resource);
            }
            if (pending.TryGetValue(cacheKey, out var existing))
            {
                cacheHits++;
                logger.Debug("resource", "resource.load_joined",
                    $"Resource '{logicalKey.Path}' joined an in-flight provider load.");
                return existing.Resource;
            }
            cacheMisses++;
            LogCacheMiss(logicalKey, resolved.Provider.Id);
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = new PendingLoad(completion.Task);
            pending.Add(cacheKey, operation);
            operation.Worker = CompleteLoadAsync(
                resolved.Provider, resolved.Location!, resourceType, cacheKey,
                logicalKey.Path, estimatedBytes, completion,
                loadLifetime?.Token ?? lifetime.RuntimeCancellationToken);
            return completion.Task;
        }
    }

    private async Task CompleteLoadAsync(
        IResourceProvider provider,
        ResourceLocation location,
        Type resourceType,
        CacheKey cacheKey,
        string resourceId,
        long estimatedBytes,
        TaskCompletionSource<object> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var resource = RequireType(
                await provider.LoadAsync(location, resourceType, cancellationToken),
                resourceType, location.Path);
            lock (gate)
            {
                loaded[cacheKey] = new CacheEntry(resource, estimatedBytes);
                EvictToBudgetLocked(cacheKey, "budget_after_async_load");
            }
            completion.TrySetResult(resource);
            logger.Debug("resource", "resource.load_completed",
                $"Resource '{resourceId}' loaded with provider '{provider.Id}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.Error("resource", "resource.load_failed",
                $"Resource '{resourceId}' failed to load with provider '{provider.Id}'.", exception);
            completion.TrySetException(exception);
            _ = completion.Task.Exception;
        }
        finally
        {
            lock (gate) pending.Remove(cacheKey);
        }
    }

    private static CacheKey RequireCacheKey(ResourceKey key, Type type, ResolvedResource resolved)
    {
        if (resolved.Location is null)
            throw new ResourceNotFoundException(key, type, resolved.Candidates);
        return new CacheKey(type, resolved.Location.ProviderId, resolved.Location.Path);
    }

    private int EvictToBudgetLocked(
        CacheKey? protectedKey,
        string reason,
        bool trimAllUnleased = false)
    {
        var total = loaded.Values.Sum(entry => entry.EstimatedBytes);
        if (budgetBytes <= 0 || total <= budgetBytes) reportedPressureBytes = 0;
        if (!trimAllUnleased && (budgetBytes <= 0 || total <= budgetBytes)) return 0;
        var removed = 0;
        foreach (var candidate in loaded
                     .Where(pair => pair.Value.LeaseCount == 0 &&
                         pair.Value.PinnedLeaseCount == 0 &&
                         (protectedKey is null || pair.Key != protectedKey))
                     .OrderBy(pair => pair.Value.LastAccessedAt).ToArray())
        {
            if (!trimAllUnleased && total <= budgetBytes) break;
            if (!loaded.Remove(candidate.Key)) continue;
            total -= candidate.Value.EstimatedBytes;
            removed++;
            evictionCount++;
            logger.Info("resource", "resource.cache_evicted",
                $"Evicted Runtime cache entry '{candidate.Key.ResolvedPath}' ({reason}).");
        }
        if (budgetBytes > 0 && total > budgetBytes)
        {
            // Lease churn during tree replacement must not report the same pressure repeatedly.
            if (reportedPressureBytes == 0 || total - reportedPressureBytes >= Math.Max(1, budgetBytes / 10))
            {
                reportedPressureBytes = total;
                var largest = string.Join("; ", loaded.OrderByDescending(pair => pair.Value.EstimatedBytes)
                    .Take(5).Select(pair => $"{pair.Key.ResolvedPath}: {pair.Value.EstimatedBytes} bytes " +
                        $"(leases={pair.Value.LeaseCount}, pinned={pair.Value.PinnedLeaseCount})"));
                logger.Warning("resource", "resource.budget_pressure",
                    $"Resource cache estimate {total} bytes exceeds budget {budgetBytes} bytes; retained entries are leased or protected. " +
                    $"Largest entries: {largest}. This is not measured process memory. " +
                    "Repeated pressure is reported only after recovery or growth of at least 10% of the budget.");
            }
        }
        else reportedPressureBytes = 0;
        return removed;
    }

    private async Task AwaitPendingAsync(CancellationToken cancellationToken)
    {
        Task[] snapshot;
        // A resource can be ready before its worker finishes diagnostics and
        // removes its pending entry. Drain the worker before resetting providers.
        lock (gate) snapshot = pending.Values.Select(operation => operation.Worker).ToArray();
        foreach (var loading in snapshot)
        {
            try { await loading.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
        }
    }

    private void ReleaseAllLeases()
    {
        IResourceLease[] leases;
        lock (gate) leases = activeLeases.ToArray();
        foreach (var lease in leases) lease.Release();
    }

    public void RenewLoadLifetime()
    {
        loadLifetime?.Dispose();
        loadLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.RuntimeCancellationToken);
    }

    private void CancelLoadLifetime()
    {
        try { loadLifetime?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static async Task<TResource> AwaitTypedAsync<TResource>(
        Task<object> loading, string path, CancellationToken cancellationToken)
        where TResource : class => Cast<TResource>(await loading.WaitAsync(cancellationToken), path);

    private static TResource Cast<TResource>(object resource, string path)
        where TResource : class => resource is TResource typed
            ? typed
            : throw new ResourceTypeMismatchException(path, typeof(TResource), resource.GetType());

    private static object RequireType(object resource, Type expectedType, string path)
    {
        if (resource is null) throw new MajoResourceException($"Resource provider returned null for '{path}'.");
        return expectedType.IsInstanceOfType(resource)
            ? resource
            : throw new ResourceTypeMismatchException(path, expectedType, resource.GetType());
    }

    private void LogCacheHit(ResourceKey key) => logger.Debug(
        "resource", "resource.cache_hit", $"Resource '{key.Path}' was served from cache.");
    private void LogCacheMiss(ResourceKey key, string providerId) => logger.Debug(
        "resource", "resource.cache_miss", $"Loading resource '{key.Path}' with provider '{providerId}'.");

    private sealed record CacheKey(Type ResourceType, string ProviderId, string ResolvedPath);
    private sealed class PendingLoad(Task<object> resource)
    {
        public Task<object> Resource { get; } = resource;
        public Task Worker { get; set; } = Task.CompletedTask;
    }
    private sealed class CacheEntry
    {
        public CacheEntry(object resource, long estimatedBytes)
        {
            Resource = resource;
            EstimatedBytes = estimatedBytes;
            LastAccessedAt = DateTimeOffset.UtcNow;
        }
        public object Resource { get; }
        public long EstimatedBytes { get; set; }
        public int LeaseCount { get; set; }
        public int PinnedLeaseCount { get; set; }
        public DateTimeOffset LastAccessedAt { get; set; }
    }
    public void CloseLoadScope()
    {
        loadLifetime?.Dispose();
        loadLifetime = null;
    }
}
