#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Actors;

public interface IActorReplacementSource
{
    Task<IPreparedActorReplacement> PrepareReplacementAsync(
        IReadOnlyList<ActorSpawnRequest> requests, CancellationToken cancellationToken);
}

public interface IPreparedActorReplacement : IAsyncDisposable
{
    IReadOnlyList<IActor> Actors { get; }
    void Commit();
}
