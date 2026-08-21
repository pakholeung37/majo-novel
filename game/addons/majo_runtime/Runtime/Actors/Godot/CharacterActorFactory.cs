using System;
using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class CharacterActorFactory : IActorFactory
{
    private readonly Node2D host;
    private readonly IOperationScheduler scheduler;
    private readonly IActorTextureLoader textureLoader;
    private readonly CharacterPositionMap positions;

    public CharacterActorFactory(
        Node2D host,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
    }

    public string FactoryId => ActorFactoryIds.Character;

    public IActor Create(ActorDefinition definition)
    {
        var view = new Sprite2D { Name = definition.Id, Centered = true };
        host.AddChild(view);
        try
        {
            return new CharacterActor(
                definition, view, scheduler, textureLoader, positions);
        }
        catch
        {
            view.QueueFree();
            throw;
        }
    }
}
