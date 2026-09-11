#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Actors;

public interface IActorManager : IRuntimeService
{
    IReadOnlyCollection<IActor> Actors { get; }

    IReadOnlyList<Majo.Runtime.Resources.ResourcePreloadEntry> GetPreloadResources(ActorSpawnRequest request) =>
        System.Array.Empty<Majo.Runtime.Resources.ResourcePreloadEntry>();

    Task<IActor> CreateAsync(
        string actorId,
        CancellationToken cancellationToken = default);

    Task<IActor> GetOrCreateAsync(
        ActorSpawnRequest request,
        CancellationToken cancellationToken = default)
        ;

    TActor GetActor<TActor>(string actorId) where TActor : class, IActor;

    bool TryGet(string actorId, out IActor? actor);

    bool Remove(string actorId);

    bool CanCreate(ActorSpawnRequest request) => true;
}
