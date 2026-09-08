#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Resources;

public sealed class ResourceManager : IResourceManager
{
    private static readonly MethodInfo CreateEntryLeaseMethod = typeof(ResourceManager)
        .GetMethod(nameof(CreateEntryLeaseCore), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly RegisteredResourceProvider registered = new();
    private readonly object gate = new();
    private readonly IResourceProvider[] providers;
    private readonly IRuntimeLifetime lifetime;
    private readonly IRuntimeLogger logger;
    private readonly Dictionary<CacheKey, CacheEntry> loaded = new();
    private readonly Dictionary<CacheKey, Task<object>> pending = new();
    private readonly HashSet<IResourceLease> activeLeases = new();
    private readonly long budgetBytes;
    private readonly bool releaseOnLastLease;
    private readonly long defaultEstimateBytes;
    private CancellationTokenSource? loadLifetime;
    private long cacheHits;
    private long cacheMisses;
    private long evictionCount;
    private long reportedPressureBytes;
    private bool isInitialized;

    public ResourceManager(
        IEnumerable<IResourceProvider> providers,
        IRuntimeLifetime lifetime,
        IRuntimeLogger? logger = null,
        long memoryBudgetBytes = 0,
        long defaultResourceEstimateBytes = 0,
        bool releaseOnLastLease = false)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = new IResourceProvider[] { registered }.Concat(providers).ToArray();
        this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        this.logger = logger ?? NullRuntimeLogger.Instance;
        ArgumentOutOfRangeException.ThrowIfNegative(memoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(defaultResourceEstimateBytes);
        budgetBytes = memoryBudgetBytes;
        this.releaseOnLastLease = releaseOnLastLease;
        defaultEstimateBytes = defaultResourceEstimateBytes;
        ValidateProviders(this.providers);
    }

    public void Register<TResource>(ResourceKey key, TResource resource, Action<TResource>? release = null)
        where TResource : class
    {
        EnsureInitialized();
        registered.Register(key, resource, release);
    }

    public bool Unregister(ResourceKey key)
    {
        EnsureInitialized();
        lock (gate)
        {
            var keys = loaded.Keys.Where(value => value.ProviderId == registered.Id && value.ResolvedPath == key.Path).ToArray();
            if (pending.Keys.Any(value => value.ProviderId == registered.Id && value.ResolvedPath == key.Path) ||
                keys.Any(value => loaded[value].LeaseCount > 0))
                throw new MajoResourceException($"Cannot unregister leased or loading resource '{key}'.");
            foreach (var cacheKey in keys) loaded.Remove(cacheKey);
            return registered.Remove(key);
        }
    }

    public bool IsPinned<TResource>(ResourceKey key, string? overridePath = null) where TResource : class
    {
        EnsureInitialized();
        var resolved = Resolve(key, typeof(TResource), overridePath);
        if (resolved.Location is null) return false;
        var cacheKey = RequireCacheKey(key, typeof(TResource), resolved);
        lock (gate) return loaded.TryGetValue(cacheKey, out var entry) && entry.PinnedLeaseCount > 0;
    }

    public int LoadedResourceCount { get { lock (gate) { return loaded.Count; } } }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (isInitialized) return;
        var initialized = new List<IResourceProvider>();
        try
        {
            foreach (var provider in providers)
            {
                await provider.InitializeAsync(cancellationToken);
                initialized.Add(provider);
            }
            RenewLoadLifetime();
            isInitialized = true;
        }
        catch (Exception initializationException)
        {
            var cleanupExceptions = new List<Exception>();
            for (var index = initialized.Count - 1; index >= 0; index--)
            {
                try { await initialized[index].ShutdownAsync(CancellationToken.None); }
                catch (Exception cleanupException) { cleanupExceptions.Add(cleanupException); }
            }
            if (cleanupExceptions.Count == 0) throw;
            throw new AggregateException(
                "Resource provider initialization failed and rollback reported errors.",
                new[] { initializationException }.Concat(cleanupExceptions));
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        CancelLoadLifetime();
        ReleaseAllLeases();
        await AwaitPendingAsync(cancellationToken);
        lock (gate) loaded.Clear();
        foreach (var provider in providers) await provider.ResetAsync(cancellationToken);
        RenewLoadLifetime();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized) return;
        CancelLoadLifetime();
        ReleaseAllLeases();
        await AwaitPendingAsync(cancellationToken);
        lock (gate) loaded.Clear();
        List<Exception>? exceptions = null;
        for (var index = providers.Length - 1; index >= 0; index--)
        {
            try { await providers[index].ShutdownAsync(cancellationToken); }
            catch (Exception exception)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(exception);
            }
        }
        loadLifetime?.Dispose();
        loadLifetime = null;
        isInitialized = false;
        if (exceptions is not null)
            throw new AggregateException("One or more resource providers failed to shut down.", exceptions);
    }

    public TResource Load<TResource>(ResourceKey key, string? overridePath = null)
        where TResource : class
    {
        EnsureInitialized();
        var resolved = Resolve(key, typeof(TResource), overridePath);
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
                loaded.Add(cacheKey, new CacheEntry(resource, Estimate(0)));
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
        string? overridePath = null,
        CancellationToken cancellationToken = default)
        where TResource : class
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(key, typeof(TResource), overridePath);
        var cacheKey = RequireCacheKey(key, typeof(TResource), resolved);
        var loading = GetOrStartLoad(key, typeof(TResource), resolved, cacheKey, Estimate(0));
        return AwaitTypedAsync<TResource>(loading, resolved.Location!.Path, cancellationToken);
    }

    public IResourceLease<TResource> LeaseAsync<TResource>(
        ResourceKey key,
        string? overridePath = null,
        long estimatedBytes = 0,
        bool pinned = false,
        CancellationToken cancellationToken = default)
        where TResource : class
    {
        EnsureInitialized();
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytes);
        return StartLease<TResource>(key, overridePath, Estimate(estimatedBytes), pinned, cancellationToken);
    }

    public IResourceGroupLease PreloadAsync(
        ResourcePreloadManifest manifest,
        ResourcePreloadContext? context = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        var leases = manifest.Entries.Select(entry => CreateEntryLease(
            ResolvePreloadEntry(entry, context ?? ResourcePreloadContext.Unlocalized),
            manifest.Pinned,
            cancellationToken)).ToArray();
        logger.Info("resource", "resource.group_started",
            $"Preload group '{manifest.Id}' started with {leases.Length} resources.");
        return new ResourceGroupLease(manifest.Id, manifest.Pinned, leases, logger);
    }

    public bool Exists<TResource>(ResourceKey key, string? overridePath = null)
        where TResource : class
    {
        EnsureInitialized();
        return Resolve(key, typeof(TResource), overridePath, false).Location is not null;
    }

    public IResourceLoader<TResource> CreateLoader<TResource>(string pathPrefix)
        where TResource : class => new ScopedResourceLoader<TResource>(this, pathPrefix);

    public int TrimCache()
    {
        EnsureInitialized();
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

    private IResourceLease CreateEntryLease(
        ResourcePreloadEntry entry,
        bool pinned,
        CancellationToken cancellationToken)
    {
        try
        {
            return (IResourceLease)CreateEntryLeaseMethod.MakeGenericMethod(entry.ResourceType)
                .Invoke(this, [entry, pinned, cancellationToken])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private ResourceLease<TResource> CreateEntryLeaseCore<TResource>(
        ResourcePreloadEntry entry,
        bool pinned,
        CancellationToken cancellationToken)
        where TResource : class => StartLease<TResource>(
            entry.Key, entry.OverridePath, Estimate(entry.EstimatedBytes), pinned, cancellationToken);

    private ResourceLease<TResource> StartLease<TResource>(
        ResourceKey key,
        string? overridePath,
        long estimatedBytes,
        bool pinned,
        CancellationToken cancellationToken)
        where TResource : class
    {
        var lease = new ResourceLease<TResource>(
            key,
            token => AcquireForLeaseAsync<TResource>(key, overridePath, estimatedBytes, pinned, token),
            ReleaseLease, ForgetLease,
            cancellationToken);
        lock (gate) activeLeases.Add(lease);
        lease.Start();
        return lease;
    }

    private async Task<LeaseAcquisition<TResource>> AcquireForLeaseAsync<TResource>(
        ResourceKey key,
        string? overridePath,
        long estimatedBytes,
        bool pinned,
        CancellationToken cancellationToken)
        where TResource : class
    {
        var resolved = Resolve(key, typeof(TResource), overridePath);
        var cacheKey = RequireCacheKey(key, typeof(TResource), resolved);
        var loading = GetOrStartLoad(key, typeof(TResource), resolved, cacheKey, estimatedBytes);
        var resource = Cast<TResource>(await loading.WaitAsync(cancellationToken), resolved.Location!.Path);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!loaded.TryGetValue(cacheKey, out var entry))
            {
                entry = new CacheEntry(resource, estimatedBytes);
                loaded.Add(cacheKey, entry);
            }
            entry.EstimatedBytes = Math.Max(entry.EstimatedBytes, estimatedBytes);
            entry.LeaseCount++;
            if (pinned) entry.PinnedLeaseCount++;
            entry.LastAccessedAt = DateTimeOffset.UtcNow;
            EvictToBudgetLocked(cacheKey, "budget_after_lease");
        }
        return new LeaseAcquisition<TResource>(resource, cacheKey, resolved.Location.Path, pinned);
    }

    private void ReleaseLease(CacheKey cacheKey, bool pinned)
    {
        lock (gate)
        {
            if (loaded.TryGetValue(cacheKey, out var entry))
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

    private void ForgetLease(IResourceLease lease)
    {
        lock (gate) activeLeases.Remove(lease);
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
                return existing;
            }
            cacheMisses++;
            LogCacheMiss(logicalKey, resolved.Provider.Id);
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(cacheKey, completion.Task);
            _ = CompleteLoadAsync(
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

    private ResourcePreloadEntry ResolvePreloadEntry(
        ResourcePreloadEntry entry,
        ResourcePreloadContext context)
    {
        entry.Validate();
        if (entry.LocaleChannel == ResourceLocaleChannel.None) return entry;
        var requestedLocale = entry.LocaleChannel == ResourceLocaleChannel.Dialogue
            ? context.DialogueLocale : context.VoiceLocale;
        var fallback = entry.LocaleChannel == ResourceLocaleChannel.Dialogue
            ? context.DialogueFallbackChain : context.VoiceFallbackChain;
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLocale);
        var root = new ResourceKey(entry.LocalizedRoot!).Path;
        if (!entry.Key.Path.StartsWith($"{root}/", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Logical key '{entry.Key.Path}' is not under localized root '{root}'.");
        var suffix = entry.Key.Path[(root.Length + 1)..];
        foreach (var locale in fallback.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = ResourceKey.Combine(root, locale, suffix);
            var candidateOverride = entry.OverridePath?.Contains(
                    LocalizedResourceResolver.LocalePlaceholder, StringComparison.Ordinal) == true
                ? entry.OverridePath.Replace(
                    LocalizedResourceResolver.LocalePlaceholder, locale, StringComparison.Ordinal)
                : null;
            if (Resolve(candidate, entry.ResourceType, candidateOverride, false).Location is not null)
            {
                logger.Debug("resource", "resource.variant_resolved",
                    $"Resource '{entry.Key.Path}' for locale '{requestedLocale}' resolved to '{candidate.Path}'.");
                return entry with { Key = candidate, OverridePath = candidateOverride };
            }
        }
        var flatOverride = RemoveLocalePlaceholder(entry.OverridePath);
        if (Resolve(entry.Key, entry.ResourceType, flatOverride, false).Location is not null)
            return entry with { OverridePath = flatOverride };
        throw CreateNotFound(entry.Key, entry.ResourceType, entry.OverridePath);
    }

    private ResolvedResource Resolve(
        ResourceKey key,
        Type resourceType,
        string? overridePath,
        bool throwIfMissing = true)
    {
        var request = new ResourceRequest(key, resourceType, overridePath);
        var candidates = new List<string>();
        foreach (var provider in providers)
        {
            candidates.AddRange(provider.GetCandidatePaths(request));
            if (provider.TryResolve(request, out var location))
                return new ResolvedResource(provider, location,
                    candidates.Distinct(StringComparer.Ordinal).ToArray());
        }
        var distinct = candidates.Distinct(StringComparer.Ordinal).ToArray();
        if (throwIfMissing)
        {
            logger.Warning("resource", "resource.resolve_failed",
                $"Resource '{key.Path}' of type '{resourceType.Name}' could not be resolved.");
            throw new ResourceNotFoundException(key, resourceType, distinct);
        }
        return new ResolvedResource(providers[0], null, distinct);
    }

    private ResourceNotFoundException CreateNotFound(ResourceKey key, Type type, string? overridePath)
    {
        var resolved = Resolve(key, type, overridePath, false);
        return new ResourceNotFoundException(key, type, resolved.Candidates);
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
        Task<object>[] snapshot;
        lock (gate) snapshot = pending.Values.ToArray();
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

    private void RenewLoadLifetime()
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

    private long Estimate(long requested) => requested > 0 ? requested : defaultEstimateBytes;

    private static string? RemoveLocalePlaceholder(string? path) => path?
        .Replace($"{LocalizedResourceResolver.LocalePlaceholder}/", string.Empty, StringComparison.Ordinal)
        .Replace($"/{LocalizedResourceResolver.LocalePlaceholder}", string.Empty, StringComparison.Ordinal)
        .Replace(LocalizedResourceResolver.LocalePlaceholder, string.Empty, StringComparison.Ordinal);

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

    private void EnsureInitialized()
    {
        if (!isInitialized) throw new MajoResourceException("ResourceManager is not initialized.");
    }

    private void LogCacheHit(ResourceKey key) => logger.Debug(
        "resource", "resource.cache_hit", $"Resource '{key.Path}' was served from cache.");
    private void LogCacheMiss(ResourceKey key, string providerId) => logger.Debug(
        "resource", "resource.cache_miss", $"Loading resource '{key.Path}' with provider '{providerId}'.");

    private static void ValidateProviders(IResourceProvider[] providers)
    {
        if (providers.Length == 0) throw new MajoResourceException("At least one resource provider is required.");
        if (providers.Any(provider => provider is null))
            throw new ArgumentException("Resource providers cannot contain null.", nameof(providers));
        if (providers.FirstOrDefault(provider => string.IsNullOrWhiteSpace(provider.Id)) is not null)
            throw new MajoResourceException("Every resource provider requires an ID.");
        var duplicate = providers.GroupBy(provider => provider.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new DuplicateResourceProviderException(duplicate.Key);
    }

    private sealed record CacheKey(Type ResourceType, string ProviderId, string ResolvedPath);
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
    private sealed record ResolvedResource(
        IResourceProvider Provider, ResourceLocation? Location, IReadOnlyList<string> Candidates);
    private sealed record LeaseAcquisition<TResource>(
        TResource Resource, CacheKey CacheKey, string ResolvedPath, bool Pinned)
        where TResource : class;

    private sealed class ResourceLease<TResource> : IResourceLease<TResource>
        where TResource : class
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation;
        private readonly Func<CancellationToken, Task<LeaseAcquisition<TResource>>> acquire;
        private readonly Action<CacheKey, bool> release;
        private readonly Action<IResourceLease> forget;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LeaseAcquisition<TResource>? acquisition;
        private bool forgotten;

        public ResourceLease(
            ResourceKey logicalKey,
            Func<CancellationToken, Task<LeaseAcquisition<TResource>>> acquire,
            Action<CacheKey, bool> release,
            Action<IResourceLease> forget,
            CancellationToken cancellationToken)
        {
            LogicalKey = logicalKey;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.acquire = acquire;
            this.release = release;
            this.forget = forget;
        }

        public string LeaseId { get; } = Guid.NewGuid().ToString("N");
        public ResourceKey LogicalKey { get; }
        public string? ResolvedPath { get; private set; }
        public Type ResourceType => typeof(TResource);
        public ResourceLeaseState State { get; private set; } = ResourceLeaseState.Loading;
        public TResource? Resource { get; private set; }
        object? IResourceLease.Resource => Resource;
        public Exception? Failure { get; private set; }
        public Task Completion => completion.Task;

        public void Start() => _ = RunAsync();

        public void Cancel() => Terminate(ResourceLeaseState.Cancelled);
        public void Release() => Terminate(ResourceLeaseState.Released);
        public void Dispose()
        {
            Release();
            cancellation.Dispose();
        }

        private void Terminate(ResourceLeaseState terminal)
        {
            LeaseAcquisition<TResource>? retained;
            lock (gate)
            {
                if (State is ResourceLeaseState.Cancelled or ResourceLeaseState.Released or ResourceLeaseState.Failed)
                    return;
                State = terminal;
                retained = acquisition;
                acquisition = null;
                Resource = null;
            }
            cancellation.Cancel();
            if (retained is not null) release(retained.CacheKey, retained.Pinned);
            completion.TrySetCanceled(cancellation.Token);
            ForgetOnce();
        }

        private async Task RunAsync()
        {
            try
            {
                var acquired = await acquire(cancellation.Token);
                lock (gate)
                {
                    if (State != ResourceLeaseState.Loading)
                    {
                        release(acquired.CacheKey, acquired.Pinned);
                        return;
                    }
                    acquisition = acquired;
                    Resource = acquired.Resource;
                    ResolvedPath = acquired.ResolvedPath;
                    State = ResourceLeaseState.Ready;
                }
                completion.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                lock (gate)
                    if (State == ResourceLeaseState.Loading) State = ResourceLeaseState.Cancelled;
                completion.TrySetCanceled(cancellation.Token);
                ForgetOnce();
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    Failure = exception;
                    State = ResourceLeaseState.Failed;
                }
                completion.TrySetException(exception);
                _ = completion.Task.Exception;
                ForgetOnce();
            }
        }

        private void ForgetOnce()
        {
            lock (gate)
            {
                if (forgotten) return;
                forgotten = true;
            }
            forget(this);
        }
    }

    private sealed class ResourceGroupLease : IResourceGroupLease
    {
        private readonly object gate = new();
        private readonly IRuntimeLogger logger;
        private ResourceLeaseState state = ResourceLeaseState.Loading;

        public ResourceGroupLease(
            string groupId,
            bool isPinned,
            IReadOnlyList<IResourceLease> resources,
            IRuntimeLogger logger)
        {
            GroupId = groupId;
            IsPinned = isPinned;
            Resources = resources;
            this.logger = logger;
            Completion = ObserveAsync();
        }

        public string LeaseId { get; } = Guid.NewGuid().ToString("N");
        public string GroupId { get; }
        public bool IsPinned { get; }
        public ResourceLeaseState State { get { lock (gate) return state; } }
        public double Progress => Resources.Count == 0 ? 1 :
            (double)Resources.Count(resource => resource.State != ResourceLeaseState.Loading) / Resources.Count;
        public IReadOnlyList<IResourceLease> Resources { get; }
        public Task Completion { get; }

        public void Cancel()
        {
            lock (gate)
            {
                if (state is ResourceLeaseState.Released or ResourceLeaseState.Cancelled) return;
                state = ResourceLeaseState.Cancelled;
            }
            foreach (var resource in Resources) resource.Cancel();
        }

        public void Release()
        {
            lock (gate)
            {
                if (state == ResourceLeaseState.Released) return;
                state = ResourceLeaseState.Released;
            }
            foreach (var resource in Resources) resource.Release();
            logger.Debug("resource", "resource.group_released",
                $"Preload group '{GroupId}' released its Runtime retention intent.");
        }

        public void Dispose() => Release();

        private async Task ObserveAsync()
        {
            try
            {
                await Task.WhenAll(Resources.Select(resource => resource.Completion));
                lock (gate) if (state == ResourceLeaseState.Loading) state = ResourceLeaseState.Ready;
                logger.Info("resource", "resource.group_completed", $"Preload group '{GroupId}' completed.");
            }
            catch (OperationCanceledException)
            {
                lock (gate) if (state == ResourceLeaseState.Loading) state = ResourceLeaseState.Cancelled;
                throw;
            }
            catch
            {
                lock (gate) state = ResourceLeaseState.Failed;
                throw;
            }
        }
    }
}

internal sealed class ScopedResourceLoader<TResource> : IResourceLoader<TResource>
    where TResource : class
{
    private readonly IResourceManager manager;
    public ScopedResourceLoader(IResourceManager manager, string pathPrefix)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        PathPrefix = new ResourceKey(pathPrefix).Path;
    }
    public string PathPrefix { get; }
    public ResourceKey GetKey(string localPath) => ResourceKey.Combine(PathPrefix, localPath);
    public bool Exists(string localPath, string? overridePath = null) =>
        manager.Exists<TResource>(GetKey(localPath), overridePath);
    public TResource Load(string localPath, string? overridePath = null) =>
        manager.Load<TResource>(GetKey(localPath), overridePath);
    public Task<TResource> LoadAsync(
        string localPath,
        string? overridePath = null,
        CancellationToken cancellationToken = default) =>
        manager.LoadAsync<TResource>(GetKey(localPath), overridePath, cancellationToken);
}
