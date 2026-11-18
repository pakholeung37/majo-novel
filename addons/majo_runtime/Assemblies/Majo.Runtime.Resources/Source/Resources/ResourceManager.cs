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
    private readonly IResourceProvider[] providers;
    private readonly IRuntimeLogger logger;
    private readonly ResourceResolver resolver;
    private readonly ResourceCache cache;
    private readonly long defaultEstimateBytes;
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
        ArgumentNullException.ThrowIfNull(lifetime);
        this.logger = logger ?? NullRuntimeLogger.Instance;
        ArgumentOutOfRangeException.ThrowIfNegative(memoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(defaultResourceEstimateBytes);
        defaultEstimateBytes = defaultResourceEstimateBytes;
        resolver = new ResourceResolver(this.providers, this.logger);
        cache = new ResourceCache(lifetime, this.logger, memoryBudgetBytes, releaseOnLastLease);
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
        return cache.RemoveResource(registered.Id, key, () => registered.Remove(key));
    }

    public bool IsPinned<TResource>(ResourceKey key, string? overridePath = null) where TResource : class
    {
        EnsureInitialized();
        return cache.IsPinned(key, typeof(TResource), resolver.Resolve(key, typeof(TResource), overridePath));
    }

    public int LoadedResourceCount => cache.LoadedResourceCount;

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
            cache.RenewLoadLifetime();
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
        await cache.DrainAsync(cancellationToken);
        foreach (var provider in providers) await provider.ResetAsync(cancellationToken);
        cache.RenewLoadLifetime();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized) return;
        await cache.DrainAsync(cancellationToken);
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
        cache.CloseLoadScope();
        isInitialized = false;
        if (exceptions is not null)
            throw new AggregateException("One or more resource providers failed to shut down.", exceptions);
    }

    public TResource Load<TResource>(ResourceKey key, string? overridePath = null) where TResource : class
    {
        EnsureInitialized();
        return cache.Load<TResource>(key, resolver.Resolve(key, typeof(TResource), overridePath), Estimate(0));
    }

    public Task<TResource> LoadAsync<TResource>(ResourceKey key, string? overridePath = null,
        CancellationToken cancellationToken = default) where TResource : class
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        return cache.LoadAsync<TResource>(key, resolver.Resolve(key, typeof(TResource), overridePath), Estimate(0), cancellationToken);
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
        var leases = new List<IResourceLease>();
        try
        {
            foreach (var entry in manifest.Entries)
                leases.Add(CreateEntryLease(
                    resolver.ResolvePreloadEntry(entry, context ?? ResourcePreloadContext.Unlocalized),
                    manifest.Pinned, cancellationToken));
            logger.Info("resource", "resource.group_started",
                $"Preload group '{manifest.Id}' started with {leases.Count} resources.");
            return new ResourceGroupLease(manifest.Id, manifest.Pinned, leases.ToArray(), logger);
        }
        catch (Exception creationException)
        {
            var cleanupExceptions = new List<Exception>();
            foreach (var lease in leases)
            {
                try { lease.Release(); }
                catch (Exception cleanupException) { cleanupExceptions.Add(cleanupException); }
            }
            if (cleanupExceptions.Count == 0) throw;
            throw new AggregateException("Preload group creation failed and cleanup reported errors.",
                new[] { creationException }.Concat(cleanupExceptions));
        }
    }

    public bool Exists<TResource>(ResourceKey key, string? overridePath = null)
        where TResource : class
    {
        EnsureInitialized();
        return resolver.Resolve(key, typeof(TResource), overridePath, false).Location is not null;
    }

    public IResourceLoader<TResource> CreateLoader<TResource>(string pathPrefix)
        where TResource : class => new ScopedResourceLoader<TResource>(this, pathPrefix);

    public int TrimCache()
    {
        EnsureInitialized();
        return cache.TrimCache();
    }

    public IReadOnlyList<ResourceDiagnosticEntry> CaptureDiagnosticResources() => cache.CaptureDiagnosticResources();
    public ResourceCacheSnapshot CaptureCacheSnapshot() => cache.CaptureCacheSnapshot();

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
            token => cache.AcquireForLeaseAsync<TResource>(key, resolver.Resolve(key, typeof(TResource), overridePath),
                estimatedBytes, pinned, token),
            cache.ForgetLease,
            cancellationToken);
        cache.TrackLease(lease);
        lease.Start();
        return lease;
    }

    private long Estimate(long requested) => requested > 0 ? requested : defaultEstimateBytes;

    private void EnsureInitialized()
    {
        if (!isInitialized) throw new MajoResourceException("ResourceManager is not initialized.");
    }
}
