#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Execution;

public interface IDirectionStateReplacementSource
{
    Task StopExecutionForRestoreAsync(CancellationToken cancellationToken);
    Task<IPreparedDirectionReplacement> PrepareReplacementAsync(
        DirectionState target, CancellationToken cancellationToken);
}

public interface IPreparedDirectionReplacement : IAsyncDisposable
{
    void Commit();
    Task Settlement { get; }
}
