#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Resources;

public interface IResourceManager : IRuntimeService
{
    int LoadedResourceCount { get; }

    TResource Load<TResource>(
        ResourceKey key,
        string? overridePath = null)
        where TResource : class;

    Task<TResource> LoadAsync<TResource>(
        ResourceKey key,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
        where TResource : class;

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
