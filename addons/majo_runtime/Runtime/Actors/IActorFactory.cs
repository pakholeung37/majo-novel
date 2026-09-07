namespace Majo.Runtime.Actors;

public interface IActorFactory
{
    string FactoryId { get; }

    /// <summary>False only when every appearance dependency is owned for the actor's lifetime.</summary>
    bool PreloadExistingAppearances => true;

    System.Collections.Generic.IReadOnlyList<Majo.Runtime.Resources.ResourcePreloadEntry> GetPreloadResources(
        ActorDefinition definition, string appearance) =>
        System.Array.Empty<Majo.Runtime.Resources.ResourcePreloadEntry>();

    IActor Create(ActorDefinition definition);

    IActor Create(ActorDefinition definition, ActorCreationMode creationMode)
    {
        var actor = Create(definition);
        if (creationMode == ActorCreationMode.Dormant)
        {
            actor.SetVisible(false);
        }

        return actor;
    }

    async System.Threading.Tasks.Task<IActor> CreateAsync(
        ActorDefinition definition,
        System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await System.Threading.Tasks.Task.FromResult(Create(definition));
    }

    async System.Threading.Tasks.Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode creationMode,
        System.Threading.CancellationToken cancellationToken)
    {
        var actor = await CreateAsync(definition, cancellationToken);
        if (creationMode == ActorCreationMode.Dormant)
        {
            actor.SetVisible(false);
        }

        return actor;
    }

    void ValidateDefinition(ActorDefinition definition)
    {
    }
}
