#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Script;

public interface IScriptCheckpointBackend
{
    ScriptCheckpoint CaptureCheckpoint();

    Task ValidateCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    void PrepareRestore(ScriptCheckpoint checkpoint);

    void CancelPreparedRestore(ScriptCheckpoint checkpoint);
}

public interface IScriptPlayback : IRuntimeService
{
    event Action<ScriptPlaybackPhaseChanged>? PhaseChanged;

    ScriptPlaybackPhase Phase { get; }

    ScriptPlaybackSpot? CurrentSpot { get; }

    bool CanCaptureCheckpoint { get; }

    ScriptCheckpoint CaptureCheckpoint();

    Task ValidateCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task RestoreCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default);
}

public interface ILineBoundarySource
{
    event Action<bool>? PreparationChanged;
    bool IsCurrentBoundary(LineBoundaryContext boundary);
    Task WaitForPreparationAsync(CancellationToken token);
    Func<LineBoundaryContext, CancellationToken, Task>? CommitLineBoundaryAsync { get; set; }
}
