using System;
using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class BackgroundActorFactory : IActorFactory
{
    private readonly Control host;
    private readonly IOperationRunner runner;
    private readonly IActorTextureLoader textureLoader;

    public BackgroundActorFactory(
        Control host,
        IOperationRunner runner,
        IActorTextureLoader textureLoader)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
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
            return new BackgroundActor(definition, container, runner, textureLoader);
        }
        catch
        {
            container.QueueFree();
            throw;
        }
    }
}
