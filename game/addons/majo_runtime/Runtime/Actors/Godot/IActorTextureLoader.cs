#nullable enable

using Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors.Godot;

public interface IActorTextureLoader
{
    Texture2D Load(string resourcePath);

    Texture2D Load(ResourceKey key, string? overridePath = null) =>
        Load(overridePath ?? key.Path);
}

public sealed class ResourceActorTextureLoader : IActorTextureLoader
{
    private readonly IResourceManager? resources;

    public ResourceActorTextureLoader()
    {
    }

    public ResourceActorTextureLoader(IResourceManager resources)
    {
        this.resources = resources ?? throw new System.ArgumentNullException(nameof(resources));
    }

    public Texture2D Load(ResourceKey key, string? overridePath = null) => resources is null
        ? Load(overridePath ?? key.Path)
        : resources.Load<Texture2D>(key, overridePath);

    public Texture2D Load(string resourcePath)
    {
        if (!ResourceLoader.Exists(resourcePath))
        {
            throw new ActorException($"Actor texture resource does not exist: '{resourcePath}'.");
        }

        return ResourceLoader.Load<Texture2D>(resourcePath) ?? throw new ActorException(
            $"Actor resource '{resourcePath}' is not a Texture2D.");
    }
}
