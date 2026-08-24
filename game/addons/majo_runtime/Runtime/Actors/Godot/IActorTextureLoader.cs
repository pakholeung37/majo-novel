#nullable enable

#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors.Godot;

public interface IActorTextureLoader : IRuntimeService
{
    Texture2D Load(string resourcePath);

    Texture2D Load(ResourceKey key, string? overridePath = null) =>
        Load(overridePath ?? key.Path);
}

public sealed class ResourceActorTextureLoader : IActorTextureLoader
{
    private readonly IResourceManager? resources;
    private readonly IMissingResourceReporter? missingResources;
    private readonly bool failOnMissingAssets;
    private Texture2D? placeholderTexture;

    public ResourceActorTextureLoader(
        bool failOnMissingAssets = false,
        IMissingResourceReporter? missingResources = null)
    {
        this.failOnMissingAssets = failOnMissingAssets;
        this.missingResources = missingResources;
    }

    public ResourceActorTextureLoader(
        IResourceManager resources,
        bool failOnMissingAssets = false,
        IMissingResourceReporter? missingResources = null)
    {
        this.resources = resources ?? throw new System.ArgumentNullException(nameof(resources));
        this.failOnMissingAssets = failOnMissingAssets;
        this.missingResources = missingResources;
    }

    public Texture2D Load(ResourceKey key, string? overridePath = null)
    {
        if (resources is null)
        {
            return Load(overridePath ?? key.Path);
        }

        if (failOnMissingAssets || resources.Exists<Texture2D>(key, overridePath))
        {
            return resources.Load<Texture2D>(key, overridePath);
        }

        var requestedPath = overridePath ?? key.Path;
        missingResources?.Report("Texture2D", key.Path, "placeholder", requestedPath);
        return GetPlaceholderTexture();
    }

    public Texture2D Load(string resourcePath)
    {
        if (!ResourceLoader.Exists(resourcePath))
        {
            if (failOnMissingAssets)
            {
                throw new ActorException(
                    $"Actor texture resource does not exist: '{resourcePath}'.");
            }

            missingResources?.Report(
                "Texture2D", resourcePath, "placeholder", resourcePath);
            return GetPlaceholderTexture();
        }

        return ResourceLoader.Load<Texture2D>(resourcePath) ?? throw new ActorException(
            $"Actor resource '{resourcePath}' is not a Texture2D.");
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        placeholderTexture?.Dispose();
        placeholderTexture = null;
        return Task.CompletedTask;
    }

    private Texture2D GetPlaceholderTexture() => placeholderTexture ??= new PlaceholderTexture2D
    {
        Size = new Vector2(128, 128),
    };
}
