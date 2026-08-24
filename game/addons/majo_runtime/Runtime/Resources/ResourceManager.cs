#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Resources;

public sealed class ResourceManager : IResourceManager
{
    private readonly object gate = new();
    private readonly IReadOnlyList<IResourceProvider> providers;
    private readonly IRuntimeLifetime lifetime;
    private readonly IRuntimeLogger logger;
    private readonly Dictionary<CacheKey, object> loaded = new();
    private readonly Dictionary<CacheKey, Task<object>> pending = new();
    private bool isInitialized;

    public ResourceManager(
        IEnumerable<IResourceProvider> providers,
        IRuntimeLifetime lifetime,
        IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers.ToArray();
        this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        this.logger = logger ?? NullRuntimeLogger.Instance;
        if (this.providers.Count == 0)
        {
            throw new MajoResourceException("At least one resource provider is required.");
        }

        if (this.providers.Any(provider => provider is null))
        {
            throw new ArgumentException(
                "Resource providers cannot contain null.", nameof(providers));
        }

        var invalidProvider = this.providers.FirstOrDefault(
            provider => string.IsNullOrWhiteSpace(provider.Id));
        if (invalidProvider is not null)
        {
            throw new MajoResourceException("Every resource provider requires an ID.");
        }

        var duplicateProvider = this.providers
            .GroupBy(provider => provider.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateProvider is not null)
        {
            throw new DuplicateResourceProviderException(duplicateProvider.Key);
        }
    }

    public int LoadedResourceCount
    {
        get
        {
            lock (gate)
            {
                return loaded.Count;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (isInitialized)
        {
            return;
        }

        var initialized = new List<IResourceProvider>();
        try
        {
            foreach (var provider in providers)
            {
                await provider.InitializeAsync(cancellationToken);
                initialized.Add(provider);
            }

            isInitialized = true;
        }
        catch (Exception initializationException)
        {
            var cleanupExceptions = new List<Exception>();
            for (var index = initialized.Count - 1; index >= 0; index--)
            {
                try
                {
                    await initialized[index].ShutdownAsync(CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    cleanupExceptions.Add(cleanupException);
                }
            }

            if (cleanupExceptions.Count == 0)
            {
                throw;
            }

            throw new AggregateException(
                "Resource provider initialization failed and rollback reported errors.",
                new[] { initializationException }.Concat(cleanupExceptions));
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await AwaitPendingAsync(cancellationToken);
        lock (gate)
        {
            loaded.Clear();
        }

        foreach (var provider in providers)
        {
            await provider.ResetAsync(cancellationToken);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return;
        }

        await AwaitPendingAsync(cancellationToken);
        lock (gate)
        {
            loaded.Clear();
        }

        List<Exception>? exceptions = null;
        for (var index = providers.Count - 1; index >= 0; index--)
        {
            try
            {
                await providers[index].ShutdownAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(exception);
            }
        }

        if (exceptions is not null)
        {
            throw new AggregateException(
                "One or more resource providers failed to shut down.", exceptions);
        }

        isInitialized = false;
    }

    public TResource Load<TResource>(
        ResourceKey key,
        string? overridePath = null)
        where TResource : class
    {
        EnsureInitialized();
        var (provider, location, candidates) = Resolve<TResource>(key, overridePath);
        if (location is null)
        {
            logger.Warning(
                "resource",
                "resource.resolve_failed",
                $"Resource '{key.Path}' of type '{typeof(TResource).Name}' could not be resolved.");
            throw new ResourceNotFoundException(key, typeof(TResource), candidates);
        }

        var cacheKey = new CacheKey(typeof(TResource), location.ProviderId, location.Path);
        lock (gate)
        {
            if (loaded.TryGetValue(cacheKey, out var cached))
            {
                logger.Debug(
                    "resource",
                    "resource.cache_hit",
                    $"Resource '{key.Path}' was served from cache.");
                return Cast<TResource>(cached, location.Path);
            }

            if (pending.ContainsKey(cacheKey))
            {
                throw new MajoResourceException(
                    $"Resource '{location.Path}' is loading asynchronously; await the existing load.");
            }

            try
            {
                logger.Debug(
                    "resource",
                    "resource.cache_miss",
                    $"Loading resource '{key.Path}' with provider '{provider.Id}'.");
                var resource = provider.Load(location, typeof(TResource));
                var typed = Cast<TResource>(resource, location.Path);
                loaded.Add(cacheKey, typed);
                logger.Debug(
                    "resource",
                    "resource.load_completed",
                    $"Resource '{key.Path}' loaded with provider '{provider.Id}'.");
                return typed;
            }
            catch (Exception exception)
            {
                logger.Error(
                    "resource",
                    "resource.load_failed",
                    $"Resource '{key.Path}' failed to load with provider '{provider.Id}'.",
                    exception);
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
        var (provider, location, candidates) = Resolve<TResource>(key, overridePath);
        if (location is null)
        {
            logger.Warning(
                "resource",
                "resource.resolve_failed",
                $"Resource '{key.Path}' of type '{typeof(TResource).Name}' could not be resolved.");
            throw new ResourceNotFoundException(key, typeof(TResource), candidates);
        }

        var cacheKey = new CacheKey(typeof(TResource), location.ProviderId, location.Path);
        Task<object> loading;
        lock (gate)
        {
            if (loaded.TryGetValue(cacheKey, out var cached))
            {
                logger.Debug(
                    "resource",
                    "resource.cache_hit",
                    $"Resource '{key.Path}' was served from cache.");
                return Task.FromResult(Cast<TResource>(cached, location.Path));
            }

            if (!pending.TryGetValue(cacheKey, out loading!))
            {
                logger.Debug(
                    "resource",
                    "resource.cache_miss",
                    $"Loading resource '{key.Path}' with provider '{provider.Id}'.");
                var completion = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                loading = completion.Task;
                pending.Add(cacheKey, loading);
                _ = CompleteLoadAsync(
                    provider,
                    location,
                    typeof(TResource),
                    cacheKey,
                    key.Path,
                    completion,
                    lifetime.SessionCancellationToken);
            }
        }

        return AwaitTypedAsync<TResource>(loading, location.Path, cancellationToken);
    }

    public bool Exists<TResource>(
        ResourceKey key,
        string? overridePath = null)
        where TResource : class
    {
        EnsureInitialized();
        var request = new ResourceRequest(key, typeof(TResource), overridePath);
        return providers.Any(provider => provider.TryResolve(request, out _));
    }

    public IResourceLoader<TResource> CreateLoader<TResource>(string pathPrefix)
        where TResource : class => new ScopedResourceLoader<TResource>(this, pathPrefix);

    private (IResourceProvider Provider, ResourceLocation? Location, string[] Candidates)
        Resolve<TResource>(ResourceKey key, string? overridePath)
        where TResource : class
    {
        var request = new ResourceRequest(key, typeof(TResource), overridePath);
        var candidates = new List<string>();
        foreach (var provider in providers)
        {
            candidates.AddRange(provider.GetCandidatePaths(request));
            if (provider.TryResolve(request, out var location))
            {
                return (provider, location, candidates.Distinct(StringComparer.Ordinal).ToArray());
            }
        }

        return (
            providers[0],
            null,
            candidates.Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task CompleteLoadAsync(
        IResourceProvider provider,
        ResourceLocation location,
        Type resourceType,
        CacheKey cacheKey,
        string resourceId,
        TaskCompletionSource<object> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var resource = RequireType(
                await provider.LoadAsync(location, resourceType, cancellationToken),
                resourceType,
                location.Path);
            lock (gate)
            {
                loaded[cacheKey] = resource;
            }

            completion.TrySetResult(resource);
            logger.Debug(
                "resource",
                "resource.load_completed",
                $"Resource '{resourceId}' loaded with provider '{provider.Id}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.Error(
                "resource",
                "resource.load_failed",
                $"Resource '{resourceId}' failed to load with provider '{provider.Id}'.",
                exception);
            completion.TrySetException(exception);
            _ = completion.Task.Exception;
        }
        finally
        {
            lock (gate)
            {
                pending.Remove(cacheKey);
            }
        }
    }

    private async Task AwaitPendingAsync(CancellationToken cancellationToken)
    {
        Task<object>[] snapshot;
        lock (gate)
        {
            snapshot = pending.Values.ToArray();
        }

        foreach (var loading in snapshot)
        {
            try
            {
                await loading.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The load caller observes provider and type failures.
            }
        }
    }

    private static async Task<TResource> AwaitTypedAsync<TResource>(
        Task<object> loading,
        string path,
        CancellationToken cancellationToken)
        where TResource : class => Cast<TResource>(
            await loading.WaitAsync(cancellationToken), path);

    private static TResource Cast<TResource>(object resource, string path)
        where TResource : class
    {
        if (resource is null)
        {
            throw new MajoResourceException(
                $"Resource provider returned null for '{path}'.");
        }

        if (resource is TResource typed)
        {
            return typed;
        }

        throw new ResourceTypeMismatchException(
            path, typeof(TResource), resource.GetType());
    }

    private static object RequireType(object resource, Type expectedType, string path)
    {
        if (resource is null)
        {
            throw new MajoResourceException(
                $"Resource provider returned null for '{path}'.");
        }

        if (expectedType.IsInstanceOfType(resource))
        {
            return resource;
        }

        throw new ResourceTypeMismatchException(path, expectedType, resource.GetType());
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new MajoResourceException("ResourceManager is not initialized.");
        }
    }

    private sealed record CacheKey(
        Type ResourceType,
        string ProviderId,
        string ResolvedPath);
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
