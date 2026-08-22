using System;
using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class BackgroundActorFactory : IActorFactory
{
    private readonly Control host;
    private readonly IOperationScheduler scheduler;
    private readonly IActorTextureLoader textureLoader;

    public BackgroundActorFactory(
        Control host,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
    }

    public string FactoryId => ActorFactoryIds.Background;

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public IActor Create(ActorDefinition definition)
    {
        var container = new Control
        {
            Name = definition.Id,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        host.AddChild(container);
        container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        try
        {
            return new BackgroundActor(definition, container, scheduler, textureLoader);
        }
        catch
        {
            container.QueueFree();
            throw;
        }
    }
}
