#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Actors;

public interface IActorManager : IRuntimeService
{
    IReadOnlyCollection<IActor> Actors { get; }

    IActor Create(string actorId);

    TActor Get<TActor>(string actorId) where TActor : class, IActor;

    bool TryGet(string actorId, out IActor? actor);

    bool Remove(string actorId);
}
