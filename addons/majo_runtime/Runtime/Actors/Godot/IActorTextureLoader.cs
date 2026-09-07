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

    Task<Texture2D> LoadAsync(
        string resourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Load(resourcePath));
    }

    Texture2D Load(ResourceKey key, string? overridePath = null) =>
        Load(overridePath ?? key.Path);

    Task<Texture2D> LoadAsync(
        ResourceKey key,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Load(key, overridePath));
    }
}

public sealed class ResourceActorTextureLoader : IActorTextureLoader, System.IDisposable
{
    private readonly IResourceManager? resources;
    private readonly IMissingResourceReporter? missingResources;
    private readonly bool failOnMissingAssets;
    private readonly ILocalizedResourceResolver? localizedResources;
    private readonly ILocaleManager? locales;
    private readonly string[] localizedRoots;
    private Texture2D? placeholderTexture;
    private ResourceRetentionScope? retention;

    public ResourceActorTextureLoader CreateRetainedScope()
    {
        if (resources is null) throw new System.InvalidOperationException("Resource-backed texture loading is required for retention.");
        return new ResourceActorTextureLoader(resources, failOnMissingAssets, missingResources,
            localizedResources, locales, localizedRoots) { retention = new ResourceRetentionScope(resources) };
    }

    private Texture2D LoadResource(ResourceKey key, string? overridePath = null)
    {
        if (retention is null) return resources!.Load<Texture2D>(key, overridePath);
        // Constructors may still use sync reads; populate the cache before taking
        // the lease so no asynchronous provider work is synchronously blocked.
        var texture = resources!.Load<Texture2D>(key, overridePath);
        retention.AcquireAsync<Texture2D>(key, overridePath).GetAwaiter().GetResult();
        return texture;
    }

    private Task<Texture2D> LoadResourceAsync(ResourceKey key, string? overridePath = null,
        CancellationToken cancellationToken = default) => retention is null
        ? resources!.LoadAsync<Texture2D>(key, overridePath, cancellationToken)
        : retention.AcquireAsync<Texture2D>(key, overridePath, cancellationToken);

    public void Dispose()
    {
        retention?.Dispose();
        placeholderTexture?.Dispose();
        placeholderTexture = null;
    }

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
                return LoadResource(
                    resolution.ResolvedKey!.Value,
                    resolution.ResolvedOverridePath);
            }

            if (failOnMissingAssets)
            {
                return LoadResource(key, overridePath);
            }
        }
        else if (failOnMissingAssets || resources.Exists<Texture2D>(key, overridePath))
        {
            return LoadResource(key, overridePath);
        }

        var requestedPath = overridePath ?? key.Path;
        missingResources?.Report("Texture2D", key.Path, "placeholder", requestedPath);
        return GetPlaceholderTexture();
    }

    public async Task<Texture2D> LoadAsync(
        ResourceKey key,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resources is null)
        {
            return Load(key, overridePath);
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
                return await LoadResourceAsync(
                    resolution.ResolvedKey!.Value,
                    resolution.ResolvedOverridePath,
                    cancellationToken);
            }

            if (failOnMissingAssets)
            {
                return await LoadResourceAsync(
                    key,
                    overridePath,
                    cancellationToken);
            }
        }
        else if (failOnMissingAssets || resources.Exists<Texture2D>(key, overridePath))
        {
            return await LoadResourceAsync(
                key,
                overridePath,
                cancellationToken);
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

        if (resources is not null)
        {
            var logicalPath = resourcePath.StartsWith("res://", System.StringComparison.Ordinal)
                ? resourcePath["res://".Length..] : resourcePath;
            return LoadResource(new ResourceKey(logicalPath), resourcePath);
        }
        return ResourceLoader.Load<Texture2D>(resourcePath) ?? throw new ActorException(
            $"Actor resource '{resourcePath}' is not a Texture2D.");
    }

    public async Task<Texture2D> LoadAsync(
        string resourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        if (resources is null)
        {
            return Load(resourcePath);
        }

        var logicalPath = resourcePath.StartsWith("res://", System.StringComparison.Ordinal)
            ? resourcePath["res://".Length..]
            : resourcePath;
        return await LoadResourceAsync(
            new ResourceKey(logicalPath),
            resourcePath,
            cancellationToken);
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
        Dispose();
        return Task.CompletedTask;
    }

    private Texture2D GetPlaceholderTexture() => placeholderTexture ??= new PlaceholderTexture2D
    {
        Size = new Vector2(128, 128),
    };
}
