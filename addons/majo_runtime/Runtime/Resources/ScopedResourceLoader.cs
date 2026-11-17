#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Resources;

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
