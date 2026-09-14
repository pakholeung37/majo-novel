#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Plugins;
using Majo.Runtime.Resources;

namespace Majo.Demos.AceAttorney.Actors;

public sealed class SpriteSheetCharacterPlugin(SpriteSheetActorRegistry registry) : IMajoPlugin
{
    public const string Factory = "sprite-sheet-character";
    public string Id => Factory;

    public void ConfigureServices(RuntimeServiceRegistry services) =>
        services.AddSingleton<SpriteSheetActorRegistry>(_ => registry);

    public void Configure(PluginRegistrationContext context) => context.ActorFactories.Register(
        Factory,
        ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
        factoryContext => new SpriteSheetCharacterActorFactory(factoryContext, registry),
        (configuration, _) =>
        {
            var data = configuration.FactoryConfiguration as SpriteSheetCharacterConfiguration ??
                throw new ActorDefinitionException(configuration.Id, "Sprite sheet configuration is required.");
            return new ActorDefinition(configuration.Id, Factory,
                new SpriteSheetDefinitionData(data.ManifestPath, data.InitialPosition));
        },
        typeof(SpriteSheetActorRegistry));
}

public sealed class SpriteSheetCharacterActorFactory : IConventionActorFactory
{
    private readonly Node2D host;
    private readonly CharacterPositionMap positions;
    private readonly SpriteSheetActorRegistry registry;
    private readonly string charactersPath;
    private readonly Dictionary<string, SpriteSheetManifest> manifests = new(StringComparer.Ordinal);

    public SpriteSheetCharacterActorFactory(ActorFactoryContext context, SpriteSheetActorRegistry registry)
    {
        host = context.CharacterRoot;
        positions = context.CharacterPositions;
        this.registry = registry;
        var resource = context.ResourceSettings;
        charactersPath = resource.CharactersPath.StartsWith("res://", StringComparison.Ordinal)
            ? resource.CharactersPath.TrimEnd('/')
            : $"{resource.RootPath.TrimEnd('/')}/{resource.CharactersPath.Trim('/')}";
    }

    public string FactoryId => SpriteSheetCharacterPlugin.Factory;
    public bool PreloadExistingAppearances => false;

    public bool TryResolveDefinition(ActorSpawnRequest request, out ActorDefinition definition)
    {
        if (request.FallbackFactoryId is not ActorFactoryIds.Character and
            not SpriteSheetCharacterPlugin.Factory)
        {
            definition = null!;
            return false;
        }
        var id = request.DefinitionId ?? request.ActorId;
        var path = $"{charactersPath}/{id}/sprite_character.json";
        if (!global::Godot.FileAccess.FileExists(path))
        {
            definition = null!;
            return false;
        }
        definition = new ActorDefinition(request.ActorId, FactoryId,
            new SpriteSheetDefinitionData(path,
                request.InitialPosition ?? CharacterPositionPreset.Center), id);
        return true;
    }

    public string GetInitialAppearance(ActorDefinition definition) =>
        GetManifest(definition).DefaultPose;

    public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(
        ActorDefinition definition, string appearance)
    {
        var data = definition.GetFactoryData<SpriteSheetDefinitionData>();
        var directory = data.ManifestPath[..data.ManifestPath.LastIndexOf('/')];
        return GetManifest(definition).GetPose(appearance).Pages.ConvertAll(page =>
        {
            var path = $"{directory}/{page}";
            return ResourcePreloadEntry.Create<Texture2D>(
                new ResourceKey(path.StartsWith("res://", StringComparison.Ordinal)
                    ? path["res://".Length..] : path), path);
        });
    }

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = GetManifest(definition);

    public Task<IActor> CreateAsync(
        ActorDefinition definition, ActorCreationMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = definition.GetFactoryData<SpriteSheetDefinitionData>();
        var view = new Node2D { Name = definition.Id, Visible = false };
        host.AddChild(view);
        try
        {
            var actor = new SpriteSheetCharacterActor(definition, view, positions,
                GetManifest(definition), data.ManifestPath[..data.ManifestPath.LastIndexOf('/')],
                data.InitialPosition, registry);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
        catch
        {
            view.QueueFree();
            throw;
        }
    }

    private SpriteSheetManifest GetManifest(ActorDefinition definition)
    {
        var path = definition.GetFactoryData<SpriteSheetDefinitionData>().ManifestPath;
        if (!manifests.TryGetValue(path, out var manifest))
        {
            if (!global::Godot.FileAccess.FileExists(path))
                throw new ActorDefinitionException(definition.Id, $"Missing sprite sheet manifest: {path}");
            manifest = SpriteSheetManifest.Parse(global::Godot.FileAccess.GetFileAsString(path));
            manifests.Add(path, manifest);
        }
        return manifest;
    }
}
