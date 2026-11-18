#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue;

namespace Majo.Runtime.Script;

public interface IScriptCheckpointRestorePreparation
{
    Task<IPreparedScriptCheckpoint> PrepareCheckpointRestoreAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken,
        DialoguePresentationOrigin origin = DialoguePresentationOrigin.CheckpointRestore);
}

public interface IScriptCheckpointReplayBackend
{
    void PrepareRestore(ScriptCheckpoint checkpoint, DialoguePresentationOrigin origin);
    Task StopForReplayAsync(CancellationToken cancellationToken);
    Task ActivatePreparedReplayAsync(
        ScriptCheckpoint checkpoint,
        DialoguePresentationOrigin origin,
        CancellationToken cancellationToken);
}

public interface IPreparedScriptCheckpoint : IAsyncDisposable
{
    void Commit();
    Task ActivateAsync(CancellationToken cancellationToken);
}
