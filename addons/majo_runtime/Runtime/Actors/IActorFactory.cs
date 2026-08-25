namespace Majo.Runtime.Actors;

public interface IActorFactory
{
    string FactoryId { get; }

    IActor Create(ActorDefinition definition);

    async System.Threading.Tasks.Task<IActor> CreateAsync(
        ActorDefinition definition,
        System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await System.Threading.Tasks.Task.FromResult(Create(definition));
    }

    void ValidateDefinition(ActorDefinition definition)
    {
    }
}
