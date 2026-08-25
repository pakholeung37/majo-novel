#nullable enable

#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;
using Majo.Runtime.Localization;
using System.Collections.Generic;
using System.Linq;

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
    private readonly ILocalizedResourceResolver? localizedResources;
    private readonly ILocaleManager? locales;
    private readonly string[] localizedRoots;
    private Texture2D? placeholderTexture;

    public ResourceActorTextureLoader(
        bool failOnMissingAssets = false,
        IMissingResourceReporter? missingResources = null)
    {
        this.failOnMissingAssets = failOnMissingAssets;
        this.missingResources = missingResources;
        localizedRoots = [];
    }

    public ResourceActorTextureLoader(
        IResourceManager resources,
        bool failOnMissingAssets = false,
        IMissingResourceReporter? missingResources = null,
        ILocalizedResourceResolver? localizedResources = null,
        ILocaleManager? locales = null,
        IEnumerable<string>? localizedRoots = null)
    {
        this.resources = resources ?? throw new System.ArgumentNullException(nameof(resources));
        this.failOnMissingAssets = failOnMissingAssets;
        this.missingResources = missingResources;
        this.localizedResources = localizedResources;
        this.locales = locales;
        this.localizedRoots = (localizedRoots ?? [])
            .Select(value => new ResourceKey(value).Path)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public Texture2D Load(ResourceKey key, string? overridePath = null)
    {
        if (resources is null)
        {
            return Load(overridePath ?? key.Path);
        }

        var root = localizedRoots.FirstOrDefault(value =>
            key.Path.StartsWith($"{value}/", System.StringComparison.Ordinal));
        if (root is not null && localizedResources is not null && locales is not null)
        {
            var resolution = localizedResources.Resolve<Texture2D>(
                key,
                root,
                locales.DialogueLocale,
                locales.GetDialogueFallbackChain(locales.DialogueLocale),
                overridePath);
            if (resolution.IsResolved)
            {
                return resources.Load<Texture2D>(
                    resolution.ResolvedKey!.Value,
                    resolution.ResolvedOverridePath);
            }

            if (failOnMissingAssets)
            {
                return resources.Load<Texture2D>(key, overridePath);
            }
        }
        else if (failOnMissingAssets || resources.Exists<Texture2D>(key, overridePath))
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
