#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;

namespace Majo.Demos.Live2D.Actors;

public sealed class Live2DCharacterActorFactory : IActorFactory
{
    private readonly Node2D host;
    private readonly CharacterPositionMap positions;
    private readonly Live2DActorRegistry registry;

    public Live2DCharacterActorFactory(ActorFactoryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        host = context.CharacterRoot;
        positions = context.CharacterPositions;
        registry = context.Services.Get<Live2DActorRegistry>();
    }

    public string FactoryId => Live2DActorIds.Factory;

    public bool PreloadExistingAppearances => false;

    public string GetInitialAppearance(ActorDefinition definition) =>
        definition.GetFactoryData<Live2DCharacterDefinitionData>().DefaultAppearance;

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
