#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Resources;
using Majo.Runtime.Actors.Godot;

namespace Majo.Runtime.Direction.Godot;

public interface IDirectionTextureLoader
{
    Task<Texture2D> LoadAsync(
        ResourceKey key,
        CancellationToken cancellationToken = default);
}

public sealed class DirectionTextureLoader : IDirectionTextureLoader
{
    private readonly IResourceManager? resources;
    private readonly IActorTextureLoader? textures;

    public DirectionTextureLoader(IResourceManager resources)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    // Share the runtime texture policy, localization, diagnostics and placeholder lifetime.
    public DirectionTextureLoader(IActorTextureLoader textures)
    {
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
    }

    public Task<Texture2D> LoadAsync(
        ResourceKey key,
        CancellationToken cancellationToken = default) =>
        textures is not null
            ? textures.LoadAsync(key, cancellationToken: cancellationToken)
            : resources!.LoadAsync<Texture2D>(key, cancellationToken: cancellationToken);
}
