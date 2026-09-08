#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Resources;

public interface IResourceManager : IRuntimeService
{
    int LoadedResourceCount { get; }

    System.Collections.Generic.IReadOnlyList<ResourceDiagnosticEntry> CaptureDiagnosticResources() =>
        System.Array.Empty<ResourceDiagnosticEntry>();

    ResourceCacheSnapshot CaptureCacheSnapshot() => new(
        LoadedResourceCount,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        System.Array.Empty<ResourceCacheEntrySnapshot>());

    TResource Load<TResource>(
        ResourceKey key,
        string? overridePath = null)
        where TResource : class;

    Task<TResource> LoadAsync<TResource>(
        ResourceKey key,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
        where TResource : class;

    IResourceLease<TResource> LeaseAsync<TResource>(
        ResourceKey key,
        string? overridePath = null,
        long estimatedBytes = 0,
        bool pinned = false,
        CancellationToken cancellationToken = default)
        where TResource : class => throw new System.NotSupportedException(
            "This resource manager does not support leases.");

    IResourceGroupLease PreloadAsync(
        ResourcePreloadManifest manifest,
        ResourcePreloadContext? context = null,
        CancellationToken cancellationToken = default) => throw new System.NotSupportedException(
            "This resource manager does not support preload groups.");

    /// <summary>Whether a resolved resource already has a persistent owner (for example a render tree).</summary>
    bool IsPinned<TResource>(ResourceKey key, string? overridePath = null) where TResource : class => false;

    int TrimCache() => 0;

    bool Exists<TResource>(
        ResourceKey key,
        string? overridePath = null)
        where TResource : class;

    IResourceLoader<TResource> CreateLoader<TResource>(string pathPrefix)
        where TResource : class;
}

public interface IResourceLoader<TResource>
    where TResource : class
{
    string PathPrefix { get; }

    ResourceKey GetKey(string localPath);

    bool Exists(string localPath, string? overridePath = null);

    TResource Load(string localPath, string? overridePath = null);

    Task<TResource> LoadAsync(
        string localPath,
        string? overridePath = null,
        CancellationToken cancellationToken = default);
}
