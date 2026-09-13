#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Resources;

namespace Majo.Demos.Live2D.Actors;

public sealed class Live2DCharacterActorFactory : IConventionActorFactory
{
    private readonly Node2D host;
    private readonly CharacterPositionMap positions;
    private readonly Live2DActorRegistry registry;
    private readonly Live2DModelMetadataCache metadata;
    private readonly string charactersPath;

    public Live2DCharacterActorFactory(ActorFactoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        host = context.CharacterRoot;
        positions = context.CharacterPositions;
        registry = context.Services.Get<Live2DActorRegistry>();
        metadata = context.Services.Get<Live2DModelMetadataCache>();
        var resources = context.ResourceSettings;
        charactersPath = resources.CharactersPath.StartsWith("res://", StringComparison.Ordinal)
            ? resources.CharactersPath.TrimEnd('/')
            : $"{resources.RootPath.TrimEnd('/')}/{resources.CharactersPath.Trim('/')}";
    }

    public string FactoryId => Live2DActorIds.Factory;

    public bool PreloadExistingAppearances => false;

    public string GetInitialAppearance(ActorDefinition definition) =>
        definition.GetFactoryData<Live2DCharacterDefinitionData>().DefaultAppearance;

    public bool TryResolveDefinition(ActorSpawnRequest request, out ActorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FallbackFactoryId is not ActorFactoryIds.Character and not Live2DActorIds.Factory)
        {
            definition = null!;
            return false;
        }

        var definitionId = request.DefinitionId ?? request.ActorId;
        var directory = $"{charactersPath}/{definitionId}/runtime";
        if (!DirAccess.DirExistsAbsolute(directory))
        {
            definition = null!;
            return false;
        }

        var matches = DirAccess.GetFilesAt(directory)
            .Where(name => name.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            definition = null!;
            return false;
        }
        if (matches.Length != 1)
            throw new ActorDefinitionException(definitionId,
                $"Live2D directory '{directory}' contains multiple model3.json files.");

        var modelPath = $"{directory}/{matches[0]}";
        var model = metadata.Get(modelPath);
        definition = new ActorDefinition(request.ActorId, FactoryId,
            new Live2DCharacterDefinitionData(modelPath, "Default",
                request.InitialPosition ?? CharacterPositionPreset.Center,
                980, new Vector2(0, -490), model.Expressions, model.Motions), definitionId);
        return true;
    }

    public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(
        ActorDefinition definition, string appearance)
    {
        var live2D = definition.GetFactoryData<Live2DCharacterDefinitionData>();
        if (!live2D.Expressions.ContainsKey(appearance))
            throw new ActorException($"Live2D actor '{definition.Id}' does not define expression '{appearance}'.");
        var directory = live2D.ModelPath[..live2D.ModelPath.LastIndexOf('/')];
        return metadata.Get(live2D.ModelPath).Textures
            .Distinct(StringComparer.Ordinal)
            .Select(texture => $"{directory}/{texture}")
            .Select(path => ResourcePreloadEntry.Create<Texture2D>(
                new ResourceKey(path.StartsWith("res://", StringComparison.Ordinal)
                    ? path["res://".Length..] : path), path))
            .ToArray();
    }

    public void ValidateDefinition(ActorDefinition definition)
    {
        var live2D = definition.GetFactoryData<Live2DCharacterDefinitionData>();
        if (!global::Godot.FileAccess.FileExists(live2D.ModelPath))
        {
            throw new ActorDefinitionException(
                definition.Id,
                $"Live2D model does not exist: '{live2D.ModelPath}'.");
        }
    }

    public Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDefinition(definition);

        var view = new Node2D
        {
            Name = definition.Id,
            Visible = false,
        };
        host.AddChild(view);
        try
        {
            var model = ClassDB.Instantiate("GDCubismUserModel").AsGodotObject() as Node2D ??
                throw new ActorException(
                    "gd_cubism class 'GDCubismUserModel' is unavailable. Check the platform GDExtension binary.");
            model.Name = "CubismModel";
            view.AddChild(model);
            AddEffect(model, "GDCubismEffectEyeBlink", "EyeBlink");
            AddEffect(model, "GDCubismEffectBreath", "Breath");

            var actor = new Live2DCharacterActor(
                definition,
                view,
                model,
                positions,
                registry);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
        catch
        {
            view.QueueFree();
            throw;
        }
    }

    private static void AddEffect(Node parent, string className, string nodeName)
    {
        var effect = ClassDB.Instantiate(className).AsGodotObject() as Node ?? throw new ActorException(
            $"gd_cubism class '{className}' is unavailable. Check the platform GDExtension binary.");
        effect.Name = nodeName;
        parent.AddChild(effect);
    }
}
