#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Direction.Rendering;

/// <summary>
/// A component prepared off-screen for one Direction transaction. Commit must be
/// synchronous, allocation-free where practical, and must not fail after Prepare succeeds.
/// </summary>
public enum DirectionOperationOwner { Scene, Global }

public interface IPreparedDirectionComponent : IAsyncDisposable
{
    DirectionOperationOwner Owner => DirectionOperationOwner.Scene;
    Task Settlement { get; }

    void Commit(DirectionCommitToken token);

    /// <summary>Releases deferred destruction after every component and cue committed.</summary>
    void ConfirmCommit() { }

    /// <summary>
    /// Compensates a partially published commit. Implementations that cannot
    /// publish partial state may keep the default no-op implementation.
    /// </summary>
    void Rollback(DirectionCommitToken token)
    {
    }

    ValueTask FinishAsync(CancellationToken cancellationToken);
}

public interface IDirectionStageBackend
{
    Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StageId stageId,
        StageState? before,
        StageState? target,
        CancellationToken cancellationToken);
}

public interface IDirectionViewBackend
{
    Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        ViewId viewId,
        ViewState? before,
        ViewState? target,
        CancellationToken cancellationToken);
}

public interface IDirectionScreenBackend
{
    Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        ScreenState before,
        ScreenState target,
        CancellationToken cancellationToken);
}

public interface IDirectionEffectBackend
{
    Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StableEffectState before,
        StableEffectState target,
        CancellationToken cancellationToken);
}
