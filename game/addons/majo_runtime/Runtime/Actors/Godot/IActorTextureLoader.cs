using Godot;

namespace Majo.Runtime.Actors.Godot;

public interface IActorTextureLoader
{
    Texture2D Load(string resourcePath);
}

public sealed class ResourceActorTextureLoader : IActorTextureLoader
{
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
