#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Rendering;

/// <summary>
/// A component prepared off-screen for one Direction transaction. Commit must be
/// synchronous, allocation-free where practical, and must not fail after Prepare succeeds.
/// </summary>
public interface IPreparedDirectionComponent : IAsyncDisposable
{
    Task Settlement { get; }

    void Commit(DirectionCommitToken token);

    ValueTask FinishAsync(CancellationToken cancellationToken);
}

public interface IDirectionStageBackend
{
    Task<IPreparedDirectionComponent> PrepareAsync(
        StageId stageId,
        StageState? before,
        StageState? target,
        CancellationToken cancellationToken);
}

public interface IDirectionViewBackend
{
    Task<IPreparedDirectionComponent> PrepareAsync(
        ViewId viewId,
        ViewState? before,
        ViewState? target,
        CancellationToken cancellationToken);
}

public interface IDirectionScreenBackend
{
    Task<IPreparedDirectionComponent> PrepareAsync(
        ScreenState before,
        ScreenState target,
        CancellationToken cancellationToken);
}

public interface IDirectionEffectBackend
{
    Task<IPreparedDirectionComponent> PrepareAsync(
        StableEffectState before,
        StableEffectState target,
        CancellationToken cancellationToken);
}
