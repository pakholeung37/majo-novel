#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Direction.Rendering;

public interface IDirectionPreparationBackend
{
    Task<IPreparedDirectionTransaction> PrepareAsync(
        DirectionExecutionPlan plan,
        CancellationToken cancellationToken);

    Task<IPreparedDirectionTransaction> PrepareAsync(
        DirectionPlan plan,
        CancellationToken cancellationToken) =>
        PrepareAsync(
            new DirectionExecutionPlan(
                plan,
                Array.Empty<DirectionResourceClaim>(),
                DirectionCompletionBarrier.Commit),
            cancellationToken);
}

public interface IPreparedDirectionTransaction : IAsyncDisposable
{
    Task Settlement { get; }

    void Commit(DirectionCommitToken token);

    /// <summary>Releases deferred destruction after every component and cue committed.</summary>
    void ConfirmCommit() { }

    /// <summary>Compensates any visible work from a failed Commit.</summary>
    void Rollback(DirectionCommitToken token)
    {
    }

    ValueTask FinishAsync(CancellationToken cancellationToken);
    ValueTask FinishSceneAsync(CancellationToken cancellationToken) => FinishAsync(cancellationToken);
}
