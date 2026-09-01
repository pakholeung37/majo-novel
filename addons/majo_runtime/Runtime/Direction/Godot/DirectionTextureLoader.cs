#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Direction.Godot;

public interface IDirectionTextureLoader
{
    Task<Texture2D> LoadAsync(
        ResourceKey key,
        CancellationToken cancellationToken = default);
}

public sealed class DirectionTextureLoader : IDirectionTextureLoader
{
    private readonly IResourceManager resources;

    public DirectionTextureLoader(IResourceManager resources)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public Task<Texture2D> LoadAsync(
        ResourceKey key,
        CancellationToken cancellationToken = default) =>
        resources.LoadAsync<Texture2D>(key, cancellationToken: cancellationToken);
}
