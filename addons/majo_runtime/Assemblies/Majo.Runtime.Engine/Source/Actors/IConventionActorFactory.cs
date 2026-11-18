#nullable enable

namespace Majo.Runtime.Actors;

/// <summary>
/// Allows a factory to infer an actor definition from project resource conventions.
/// Explicit definitions always take precedence.
/// </summary>
public interface IConventionActorFactory : IActorFactory
{
    bool TryResolveDefinition(
        ActorSpawnRequest request,
        out ActorDefinition definition);
}
