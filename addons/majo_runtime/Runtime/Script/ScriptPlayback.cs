#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;

namespace Majo.Runtime.Script;

public sealed class ScriptPlayback : IScriptPlayback
{
    private readonly IDialoguePlayer dialogue;
    private readonly IDialoguePresentation presentation;
    private readonly IScriptCheckpointBackend checkpointBackend;
    private readonly IRuntimeLogger logger;
    private bool isInitialized;

    public ScriptPlayback(
        IDialoguePlayer dialogue,
        IDialoguePresentation presentation,
        IScriptCheckpointBackend checkpointBackend,
        IRuntimeLogger? logger = null)
    {
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.presentation = presentation ??
            throw new ArgumentNullException(nameof(presentation));
        this.checkpointBackend = checkpointBackend ??
            throw new ArgumentNullException(nameof(checkpointBackend));
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public event Action<ScriptPlaybackPhaseChanged>? PhaseChanged;

    public ScriptPlaybackPhase Phase { get; private set; } = ScriptPlaybackPhase.Idle;

    public ScriptPlaybackSpot? CurrentSpot =>
        dialogue.CurrentProjectId is { } project &&
        dialogue.CurrentNode is { } node &&
        dialogue.CurrentLineId is { } line
            ? new ScriptPlaybackSpot(new ScriptId(project, node), line)
            : null;

    public bool CanCaptureCheckpoint =>
        Phase == ScriptPlaybackPhase.AwaitingAdvance &&
        CurrentSpot is not null;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        dialogue.StateChanged += HandleDialogueStateChanged;
        dialogue.LinePresented += HandleLinePresented;
        dialogue.OptionsPresented += HandleOptionsPresented;
        presentation.LineDisplayCompleted += HandleLineDisplayCompleted;
        presentation.LineDismissed += HandleLineDismissed;
        isInitialized = true;
        ProjectDialogueState();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetPhase(ScriptPlaybackPhase.Idle);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }

        dialogue.StateChanged -= HandleDialogueStateChanged;
        dialogue.LinePresented -= HandleLinePresented;
        dialogue.OptionsPresented -= HandleOptionsPresented;
        presentation.LineDisplayCompleted -= HandleLineDisplayCompleted;
        presentation.LineDismissed -= HandleLineDismissed;
        isInitialized = false;
        SetPhase(ScriptPlaybackPhase.Idle);
        return Task.CompletedTask;
    }

    public ScriptCheckpoint CaptureCheckpoint()
    {
        if (!CanCaptureCheckpoint)
        {
            throw new ScriptCheckpointException(
                $"Cannot capture script checkpoint while script is in phase '{Phase}'.");
        }

        var checkpoint = checkpointBackend.CaptureCheckpoint();
        logger.Debug(
            "script",
            "script.checkpoint_captured",
            $"Script checkpoint captured for spot '{checkpoint.Spot.Script.ProjectId}:{checkpoint.Spot.Script.NodeName}#{checkpoint.Spot.LineId}'.");
        return checkpoint;
    }

    public async Task ValidateCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        try
        {
            await checkpointBackend.ValidateCheckpointAsync(
                checkpoint,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.Warning(
                "script",
                "script.checkpoint_preflight_failed",
                $"Script checkpoint preflight validation failed for spot '{checkpoint.Spot.Script.ProjectId}:{checkpoint.Spot.Script.NodeName}#{checkpoint.Spot.LineId}': {exception.Message}",
                exception);
            throw;
        }
    }

    public async Task RestoreCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        logger.Info(
            "script",
            "script.checkpoint_restore_started",
            $"Restoring script checkpoint for spot '{checkpoint.Spot.Script.ProjectId}:{checkpoint.Spot.Script.NodeName}#{checkpoint.Spot.LineId}'.");

        await ValidateCheckpointAsync(checkpoint, cancellationToken);
        await dialogue.StopAsync(cancellationToken);
        checkpointBackend.PrepareRestore(checkpoint);
        try
        {
            await dialogue.StartAsync(
                new DialogueStartRequest(
                    checkpoint.Spot.Script.ProjectId,
                    checkpoint.Spot.Script.NodeName),
                cancellationToken);
            await WaitForRestoredBoundaryAsync(checkpoint, cancellationToken);
            logger.Info(
                "script",
                "script.checkpoint_restore_completed",
                $"Script checkpoint restored successfully for spot '{checkpoint.Spot.Script.ProjectId}:{checkpoint.Spot.Script.NodeName}#{checkpoint.Spot.LineId}'.");
        }
        catch
        {
            checkpointBackend.CancelPreparedRestore(checkpoint);
            throw;
        }
    }

    private async Task WaitForRestoredBoundaryAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var settled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void ObservePhase(ScriptPlaybackPhase phase)
        {
            if (phase == ScriptPlaybackPhase.AwaitingAdvance)
            {
                var spot = CurrentSpot;
                if (spot is not null &&
                    string.Equals(
                        spot.Script.ProjectId,
                        checkpoint.Spot.Script.ProjectId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        spot.Script.NodeName,
                        checkpoint.Spot.Script.NodeName,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        spot.LineId,
                        checkpoint.Spot.LineId,
                        StringComparison.Ordinal))
                {
                    settled.TrySetResult();
                }
                else
                {
                    settled.TrySetException(new ScriptCheckpointException(
                        "Checkpoint restore reached an unexpected line boundary: " +
                        $"expected '{checkpoint.Spot.Script.ProjectId}:" +
                        $"{checkpoint.Spot.Script.NodeName}#{checkpoint.Spot.LineId}', " +
                        $"actual '{spot?.Script.ProjectId ?? "<none>"}:" +
                        $"{spot?.Script.NodeName ?? "<none>"}#{spot?.LineId ?? "<none>"}'."));
                }
            }
            else if (phase is ScriptPlaybackPhase.Faulted or ScriptPlaybackPhase.Idle)
            {
                settled.TrySetException(new ScriptCheckpointException(
                    $"Checkpoint restore ended in phase '{phase}' before its current line " +
                    "reached the awaiting-advance boundary."));
            }
        }

        void HandlePhaseChanged(ScriptPlaybackPhaseChanged change) =>
            ObservePhase(change.Current);

        PhaseChanged += HandlePhaseChanged;
        try
        {
            ObservePhase(Phase);
            await settled.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            PhaseChanged -= HandlePhaseChanged;
        }
    }

    private void HandleDialogueStateChanged(DialogueStateChanged _) => ProjectDialogueState();

    private void HandleLinePresented(DialogueLine _) => ProjectDialogueState();

    private void HandleOptionsPresented(System.Collections.Generic.IReadOnlyList<DialogueOption> _) =>
        ProjectDialogueState();

    private void HandleLineDisplayCompleted() => ProjectDialogueState();

    private void HandleLineDismissed() => ProjectDialogueState();

    private void ProjectDialogueState()
    {
        SetPhase(dialogue.State switch
        {
            DialogueSessionState.Idle => ScriptPlaybackPhase.Idle,
            DialogueSessionState.Loading => ScriptPlaybackPhase.Loading,
            DialogueSessionState.Running when dialogue.IsPresentingOptions =>
                ScriptPlaybackPhase.PresentingOptions,
            DialogueSessionState.Running when presentation.IsLineDisplayComplete =>
                ScriptPlaybackPhase.AwaitingAdvance,
            DialogueSessionState.Running when presentation.IsLineActive =>
                ScriptPlaybackPhase.PresentingLine,
            DialogueSessionState.Running => ScriptPlaybackPhase.Executing,
            DialogueSessionState.Stopping => ScriptPlaybackPhase.Stopping,
            DialogueSessionState.Faulted => ScriptPlaybackPhase.Faulted,
            _ => ScriptPlaybackPhase.Faulted,
        });
    }

    private void SetPhase(ScriptPlaybackPhase phase)
    {
        if (Phase == phase)
        {
            return;
        }

        var previous = Phase;
        Phase = phase;
        logger.Debug(
            "script",
            "script.phase_changed",
            $"Script playback phase changed: {previous} -> {phase}.");
        PhaseChanged?.Invoke(new ScriptPlaybackPhaseChanged(previous, phase));
    }
}
