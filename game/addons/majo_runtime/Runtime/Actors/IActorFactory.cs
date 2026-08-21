namespace Majo.Runtime.Actors;

public interface IActorFactory
{
    string FactoryId { get; }

    IActor Create(ActorDefinition definition);
}
