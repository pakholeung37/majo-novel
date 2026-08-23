#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;

namespace Majo.Runtime.Script;

public sealed class ScriptPlayback : IScriptPlayback
{
    private readonly IDialoguePlayer dialogue;
    private readonly IDialoguePresentation presentation;
    private readonly IScriptCheckpointBackend checkpointBackend;
    private bool isInitialized;

    public ScriptPlayback(
        IDialoguePlayer dialogue,
        IDialoguePresentation presentation,
        IScriptCheckpointBackend checkpointBackend)
    {
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.presentation = presentation ??
            throw new ArgumentNullException(nameof(presentation));
        this.checkpointBackend = checkpointBackend ??
            throw new ArgumentNullException(nameof(checkpointBackend));
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
        if (isInitialized)
        {
            dialogue.StateChanged -= HandleDialogueStateChanged;
            dialogue.LinePresented -= HandleLinePresented;
            dialogue.OptionsPresented -= HandleOptionsPresented;
            presentation.LineDisplayCompleted -= HandleLineDisplayCompleted;
            presentation.LineDismissed -= HandleLineDismissed;
            isInitialized = false;
        }

        SetPhase(ScriptPlaybackPhase.Idle);
        return Task.CompletedTask;
    }

    public ScriptCheckpoint CaptureCheckpoint()
    {
        if (!CanCaptureCheckpoint)
        {
            throw new ScriptCheckpointException(
                $"A script checkpoint cannot be captured while playback is '{Phase}'.");
        }

        return checkpointBackend.CaptureCheckpoint();
    }

    public Task ValidateCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return checkpointBackend.ValidateCheckpointAsync(checkpoint, cancellationToken);
    }

    public async Task RestoreCheckpointAsync(
        ScriptCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
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
        }
        catch
        {
            checkpointBackend.CancelPreparedRestore(checkpoint);
            throw;
        }
    }

    private void HandleDialogueStateChanged(DialogueStateChanged _) => ProjectDialogueState();

    private void HandleLinePresented(DialogueLine _) =>
        SetPhase(ScriptPlaybackPhase.PresentingLine);

    private void HandleOptionsPresented(System.Collections.Generic.IReadOnlyList<DialogueOption> _) =>
        SetPhase(ScriptPlaybackPhase.PresentingOptions);

    private void HandleLineDisplayCompleted() =>
        SetPhase(ScriptPlaybackPhase.AwaitingAdvance);

    private void HandleLineDismissed() => SetPhase(ScriptPlaybackPhase.Executing);

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
        PhaseChanged?.Invoke(new ScriptPlaybackPhaseChanged(previous, phase));
    }
}
