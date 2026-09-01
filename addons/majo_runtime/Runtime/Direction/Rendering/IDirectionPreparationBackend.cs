#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Rendering;

public interface IDirectionPreparationBackend
{
    Task<IPreparedDirectionTransaction> PrepareAsync(
        DirectionPlan plan,
        CancellationToken cancellationToken);
}

public interface IPreparedDirectionTransaction : IAsyncDisposable
{
    Task Settlement { get; }

    void Commit(DirectionCommitToken token);

    ValueTask FinishAsync(CancellationToken cancellationToken);
}
