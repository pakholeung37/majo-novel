#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Timing;

namespace Majo.Demos.Manosaba.Actors.Layered;


public sealed class LayeredCharacterActorFactory : IConventionActorFactory
{
    private const string ManifestFileName = "layered_character.json";
    private readonly Node2D host;
    private readonly IActorTextureLoader textureLoader;
    private readonly IFrameClock? frameClock;
    private readonly CharacterPositionMap positions;
    private readonly IActorAppearanceTransitionService transitions;
    private readonly string charactersPath;
    private readonly LayeredCharacterManifestCache manifests;
    private readonly string multiplyShaderPath = LayeredCharacterActor.AlphaAwareMultiplyShaderPath;
    private readonly string compositeShaderPath = LayeredCharacterActor.LayeredCompositeShaderPath;

    public LayeredCharacterActorFactory(ActorFactoryContext context)
        : this(context, LayeredCharacterActor.AlphaAwareMultiplyShaderPath,
            LayeredCharacterActor.LayeredCompositeShaderPath)
    {
    }

    public LayeredCharacterActorFactory(ActorFactoryContext context, string multiplyShaderPath, string compositeShaderPath)
        : this(
            context.CharacterRoot,
            context.TextureLoader,
            context.FrameClock,
            context.CharacterPositions,
            context.ResourceSettings.RootPath,
            context.ResourceSettings.CharactersPath,
            context.AppearanceTransitions,
            context.Services.Resolve<LayeredCharacterManifestCache>())
    {
        this.multiplyShaderPath = multiplyShaderPath;
        this.compositeShaderPath = compositeShaderPath;
    }

    public LayeredCharacterActorFactory(
        Node2D host,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        string resourceRootPath,
        string charactersPath,
        IActorAppearanceTransitionService? transitions = null,
        LayeredCharacterManifestCache? manifests = null)
        : this(
            host,
            textureLoader,
            null!,
            positions,
            resourceRootPath,
            charactersPath,
            transitions, manifests)
    {
    }

    public LayeredCharacterActorFactory(
        Node2D host,
        IActorTextureLoader textureLoader,
        IFrameClock frameClock,
        CharacterPositionMap positions,
        string resourceRootPath,
        string charactersPath,
        IActorAppearanceTransitionService? transitions = null,
        LayeredCharacterManifestCache? manifests = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        this.frameClock = frameClock;
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        this.transitions = transitions ?? new GodotActorAppearanceTransitionService();
        this.manifests = manifests ?? new LayeredCharacterManifestCache();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(charactersPath);
        this.charactersPath = ResolveCharactersPath(resourceRootPath, charactersPath);
    }


    public string FactoryId => LayeredActorIds.Factory;

    // Creation loads every atlas; the actor's layer sprites retain them even if
    // ResourceManager later evicts its separate cache entry.
    public bool PreloadExistingAppearances => false;

    public string GetInitialAppearance(ActorDefinition definition) =>
        GetManifest(definition.GetFactoryData<LayeredCharacterDefinitionData>().ManifestPath).DefaultAppearance;

    public bool TryResolveDefinition(
        ActorSpawnRequest request,
        out ActorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FallbackFactoryId is not ActorFactoryIds.Character and
            not LayeredActorIds.Factory)
        {
            definition = null!;
            return false;
        }

        var definitionId = request.DefinitionId ?? request.ActorId;
        var manifestPath = $"{charactersPath}/{definitionId}/{ManifestFileName}";
        if (!global::Godot.FileAccess.FileExists(manifestPath))
        {
            definition = null!;
            return false;
        }

        definition = new ActorDefinition(
            request.ActorId,
            FactoryId,
            new LayeredCharacterDefinitionData(
                definitionId, manifestPath,
                request.InitialPosition ?? CharacterPositionPreset.Center),
            definitionId);
        return true;
    }

    public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance)
    {
        var path = definition.GetFactoryData<LayeredCharacterDefinitionData>().ManifestPath;
        var directory = GetDirectory(path);
        return GetManifest(path).Layers.Select(layer => layer.Atlas).Distinct(StringComparer.Ordinal)
            .Select(atlas => $"{directory}/{atlas}")
            .Select(atlasPath => ResourcePreloadEntry.Create<Texture2D>(
                new ResourceKey(atlasPath.StartsWith("res://", StringComparison.Ordinal)
                    ? atlasPath["res://".Length..] : atlasPath), atlasPath)).ToArray();
    }

    public void ValidateDefinition(ActorDefinition definition)
    {
        var configuration = definition.GetFactoryData<LayeredCharacterDefinitionData>();
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.AssetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ManifestPath);
    }

    public async Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken)
    {
        var configuration = definition.GetFactoryData<LayeredCharacterDefinitionData>();
        var manifest = await GetManifestAsync(
            configuration.ManifestPath,
            cancellationToken);
        ValidateManifestId(
            string.IsNullOrWhiteSpace(configuration.AssetId)
                ? definition.DefinitionId
                : configuration.AssetId,
            manifest);
        var manifestDirectory = GetDirectory(configuration.ManifestPath);
        var atlasNames = manifest.Layers
            .Select(layer => layer.Atlas)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var loading = atlasNames.ToDictionary(
            atlas => atlas,
            atlas => textureLoader.LoadAsync(
                $"{manifestDirectory}/{atlas}",
                cancellationToken),
            StringComparer.Ordinal);
        await Task.WhenAll(loading.Values);
        cancellationToken.ThrowIfCancellationRequested();
        var atlases = loading.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Result,
            StringComparer.Ordinal);

        var view = new Sprite2D
        {
            Name = definition.Id,
            Centered = true,
            Visible = false,
        };
        host.AddChild(view);
        LayeredCharacterActor? actor = null;
        try
        {
            actor = new LayeredCharacterActor(
                definition,
                view,
                textureLoader,
                positions,
                manifest,
                configuration.ManifestPath,
                configuration.InitialPosition,
                LayeredCharacterActor.CreateAlphaAwareMultiplyMaterial(multiplyShaderPath),
                buildLayerViews: false,
                transitions: transitions,
                compositeShaderPath: compositeShaderPath);

            await actor.BuildLayerViewsAsync(
                manifest,
                atlases,
                frameClock,
                cancellationToken);
            await actor.WarmAppearanceRenderingAsync(cancellationToken);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return actor;
        }
        catch
        {
            if (actor is not null)
            {
                actor.Destroy();
            }
            else
            {
                view.QueueFree();
            }

            throw;
        }
    }

    private LayeredCharacterManifest GetManifest(string path) => manifests.Get(path);

    private Task<LayeredCharacterManifest> GetManifestAsync(string path, CancellationToken token) =>
        manifests.GetAsync(path, token);

    private static void ValidateManifestId(
        string actorId,
        LayeredCharacterManifest manifest)
    {
        if (!string.Equals(manifest.Id, actorId, StringComparison.Ordinal))
        {
            throw new ActorDefinitionException(
                actorId,
                $"Layered character manifest ID is '{manifest.Id}'.");
        }
    }

    private static string GetDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash <= "res:/".Length)
        {
            throw new ActorException($"Layered character manifest path '{path}' is invalid.");
        }

        return path[..slash];
    }

    private static string ResolveCharactersPath(
        string resourceRootPath,
        string configuredCharactersPath)
    {
        if (configuredCharactersPath.StartsWith("res://", StringComparison.Ordinal))
        {
            return configuredCharactersPath.TrimEnd('/');
        }

        return $"{resourceRootPath.TrimEnd('/')}/{configuredCharactersPath.Trim('/')}";
    }
}
