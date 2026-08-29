#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Input;
using Majo.Runtime.Script;
using Majo.Runtime.State;

namespace Majo.Runtime.Travel;

public sealed class DialogueTravelManager : IDialogueTravelManager
{
    private readonly object sync = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly List<DialogueTravelEntry> entries = new();
    private readonly IStateManager state;
    private readonly IGameSessionCoordinator sessions;
    private readonly IScriptPlayback script;
    private readonly IDialoguePlayer dialogue;
    private readonly IPlaybackModeManager playback;
    private readonly IInputManager? input;
    private readonly IRuntimeLogger logger;
    private IInputHandle? travelStepInput;
    private DialogueLine? currentLine;
    private int currentIndex = -1;
    private long nextSequence;
    private long lineGeneration;
    private long capturedLineGeneration = -1;
    private long timelineGeneration;
    private bool capturePendingAfterHotReload;
    private bool isTravelling;
    private bool isInitialized;

    public DialogueTravelManager(
        IStateManager state,
        IGameSessionCoordinator sessions,
        IScriptPlayback script,
        IDialoguePlayer dialogue,
        IPlaybackModeManager playback,
        int capacity,
        IInputManager? input = null,
        IRuntimeLogger? logger = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.script = script ?? throw new ArgumentNullException(nameof(script));
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.input = input;
        this.logger = logger ?? NullRuntimeLogger.Instance;
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
    }

    public event Action<DialogueTravelTimelineChanged>? TimelineChanged;

    public event Action<DialogueTravelCompleted>? TravelCompleted;

    public event Action<DialogueTravelFailed>? TravelFailed;

    public IReadOnlyList<DialogueTravelEntry> Entries
    {
        get
        {
            lock (sync)
            {
                return entries.ToArray();
            }
        }
    }

    public int CurrentIndex
    {
        get
        {
            lock (sync)
            {
                return currentIndex;
            }
        }
    }

    public int Capacity { get; }

    public bool IsTravelling
    {
        get
        {
            lock (sync)
            {
                return isTravelling;
            }
        }
    }

    public bool CanStepBack
    {
        get
        {
            lock (sync)
            {
                return currentIndex > 0;
            }
        }
    }

    public bool CanStepForward
    {
        get
        {
            lock (sync)
            {
                return currentIndex >= 0 && currentIndex < entries.Count - 1;
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        dialogue.LinePresented += HandleLinePresented;
        script.PhaseChanged += HandleScriptPhaseChanged;
        playback.PauseChanged += HandlePlaybackPauseChanged;
        sessions.SessionChanged += HandleSessionChanged;
        isInitialized = true;
        capturePendingAfterHotReload = playback.PauseReasons.Contains(
            PlaybackPauseReason.HotReload);
        EnsureTravelStepInput();

        if (script.Phase == ScriptPlaybackPhase.AwaitingAdvance)
        {
            QueueBoundaryCapture();
        }
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await mutations.WaitAsync(cancellationToken);
        try
        {
            ClearCore();
            EnsureTravelStepInput();
        }
        finally
        {
            mutations.Release();
        }
        PublishTimelineChanged();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return;
        }

        dialogue.LinePresented -= HandleLinePresented;
        script.PhaseChanged -= HandleScriptPhaseChanged;
        playback.PauseChanged -= HandlePlaybackPauseChanged;
        sessions.SessionChanged -= HandleSessionChanged;
        DisposeTravelStepInput();
        isInitialized = false;

        await mutations.WaitAsync(cancellationToken);
        try
        {
            ClearCore();
        }
        finally
        {
            mutations.Release();
        }
    }

    public Task<bool> StepBackAsync(CancellationToken cancellationToken = default) =>
        TravelRelativeAsync(-1, cancellationToken);

    public Task<bool> StepForwardAsync(CancellationToken cancellationToken = default) =>
        TravelRelativeAsync(1, cancellationToken);

    public async Task<bool> TravelToAsync(
        long sequence,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await mutations.WaitAsync(cancellationToken);
        DialogueTravelEntry? target = null;
        var targetIndex = -1;
        var previousIndex = -1;
        try
        {
            lock (sync)
            {
                targetIndex = entries.FindIndex(entry => entry.Sequence == sequence);
                if (targetIndex < 0)
                {
                    return false;
                }

                previousIndex = currentIndex;
                if (targetIndex == currentIndex)
                {
                    return true;
                }

                target = entries[targetIndex];
                isTravelling = true;
            }

            playback.SetMode(PlaybackMode.Manual);
            using var pause = playback.AcquirePause(PlaybackPauseReason.External);
            await sessions.RollbackAsync(target.Snapshot, cancellationToken);

            lock (sync)
            {
                currentIndex = entries.FindIndex(entry => entry.Sequence == target.Sequence);
            }
            logger.Info(
                "travel",
                "travel.completed",
                $"Dialogue travelled to '{target.Spot.Script.ProjectId}:" +
                $"{target.Spot.Script.NodeName}#{target.Spot.LineId}'.");
            TravelCompleted?.Invoke(new DialogueTravelCompleted(
                target,
                previousIndex,
                CurrentIndex));
            PublishTimelineChanged();
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning(
                "travel",
                "travel.failed",
                $"Dialogue travel failed: {exception.Message}",
                exception);
            TravelFailed?.Invoke(new DialogueTravelFailed(target, exception));
            throw;
        }
        finally
        {
            lock (sync)
            {
                isTravelling = false;
            }
            mutations.Release();
        }
    }

    public void Clear()
    {
        ClearCore();
        PublishTimelineChanged();
    }

    private async Task<bool> TravelRelativeAsync(
        int offset,
        CancellationToken cancellationToken)
    {
        long sequence;
        lock (sync)
        {
            var targetIndex = currentIndex + offset;
            if (targetIndex < 0 || targetIndex >= entries.Count)
            {
                return false;
            }
            sequence = entries[targetIndex].Sequence;
        }

        return await TravelToAsync(sequence, cancellationToken);
    }

    private void HandleLinePresented(DialogueLine line)
    {
        lock (sync)
        {
            currentLine = line;
            lineGeneration++;
        }
    }

    private void HandleScriptPhaseChanged(ScriptPlaybackPhaseChanged change)
    {
        if (change.Current == ScriptPlaybackPhase.AwaitingAdvance)
        {
            QueueBoundaryCapture();
        }
    }

    private void HandlePlaybackPauseChanged(PlaybackPauseStateChanged change)
    {
        if (change.Reasons.Contains(PlaybackPauseReason.HotReload))
        {
            capturePendingAfterHotReload = true;
            return;
        }

        if (capturePendingAfterHotReload &&
            !change.IsPaused &&
            script.Phase == ScriptPlaybackPhase.AwaitingAdvance)
        {
            capturePendingAfterHotReload = false;
            QueueBoundaryCapture();
        }
    }

    private void HandleSessionChanged(GameSessionChanged change)
    {
        if (!change.BeganNewSession)
        {
            return;
        }

        if (change.Transition != GameSessionTransition.LoadGame)
        {
            Clear();
            return;
        }

        ClearCore(preserveCurrentLine: true);
        PublishTimelineChanged();
        if (script.Phase == ScriptPlaybackPhase.AwaitingAdvance)
        {
            QueueBoundaryCapture(allowCheckpointRestore: true);
        }
    }

    private void QueueBoundaryCapture(bool allowCheckpointRestore = false)
    {
        DialogueLine? line;
        long capturedLineGeneration;
        long capturedTimelineGeneration;
        lock (sync)
        {
            if (!isInitialized || isTravelling ||
                playback.PauseReasons.Contains(PlaybackPauseReason.HotReload) ||
                currentLine is null ||
                this.capturedLineGeneration == lineGeneration ||
                currentLine.Origin is not DialoguePresentationOrigin.Normal and
                    not DialoguePresentationOrigin.HotReloadReplay &&
                (!allowCheckpointRestore ||
                 currentLine.Origin != DialoguePresentationOrigin.CheckpointRestore))
            {
                return;
            }

            line = currentLine;
            capturedLineGeneration = lineGeneration;
            capturedTimelineGeneration = timelineGeneration;
        }

        _ = CaptureBoundaryAsync(
            line,
            capturedLineGeneration,
            capturedTimelineGeneration);
    }

    private async Task CaptureBoundaryAsync(
        DialogueLine line,
        long capturedLineGeneration,
        long capturedTimelineGeneration)
    {
        await mutations.WaitAsync();
        try
        {
            ScriptPlaybackSpot? spot;
            lock (sync)
            {
                if (!isInitialized || isTravelling ||
                    capturedLineGeneration != lineGeneration ||
                    capturedTimelineGeneration != timelineGeneration ||
                    this.capturedLineGeneration == capturedLineGeneration)
                {
                    return;
                }
                spot = script.CurrentSpot;
            }

            if (script.Phase != ScriptPlaybackPhase.AwaitingAdvance ||
                playback.PauseReasons.Contains(PlaybackPauseReason.HotReload) ||
                spot is null ||
                !string.Equals(spot.LineId, line.Id, StringComparison.Ordinal))
            {
                return;
            }

            var snapshot = await state.CaptureGameAsync();
            DialogueTravelEntry entry;

            lock (sync)
            {
                if (!isInitialized || isTravelling ||
                    capturedLineGeneration != lineGeneration ||
                    capturedTimelineGeneration != timelineGeneration ||
                    this.capturedLineGeneration == capturedLineGeneration)
                {
                    return;
                }

                var replacesHotReloadReplay =
                    line.Origin == DialoguePresentationOrigin.HotReloadReplay &&
                    currentIndex >= 0 &&
                    currentIndex < entries.Count &&
                    entries[currentIndex].Spot == spot;
                var sequence = replacesHotReloadReplay
                    ? entries[currentIndex].Sequence
                    : ++nextSequence;
                entry = new DialogueTravelEntry(
                    sequence,
                    spot,
                    snapshot,
                    line.Speaker,
                    line.Text.Text,
                    snapshot.CapturedAt);

                if (replacesHotReloadReplay)
                {
                    entries[currentIndex] = entry;
                }
                else
                {
                    if (currentIndex >= 0 && currentIndex < entries.Count - 1)
                    {
                        entries.RemoveRange(
                            currentIndex + 1,
                            entries.Count - currentIndex - 1);
                    }

                    entries.Add(entry);
                    if (entries.Count > Capacity)
                    {
                        entries.RemoveRange(0, entries.Count - Capacity);
                    }
                    currentIndex = entries.Count - 1;
                }
                this.capturedLineGeneration = capturedLineGeneration;
            }

            logger.Debug(
                "travel",
                "travel.checkpoint_captured",
                $"Dialogue travel checkpoint captured for '{spot.Script.ProjectId}:" +
                $"{spot.Script.NodeName}#{spot.LineId}'.");
            PublishTimelineChanged();
        }
        catch (StateEligibilityException exception)
        {
            logger.Debug(
                "travel",
                "travel.capture_skipped",
                $"Dialogue travel checkpoint was skipped: {exception.Message}");
        }
        catch (Exception exception)
        {
            logger.Warning(
                "travel",
                "travel.capture_failed",
                $"Dialogue travel checkpoint capture failed: {exception.Message}",
                exception);
            TravelFailed?.Invoke(new DialogueTravelFailed(null, exception));
        }
        finally
        {
            mutations.Release();
        }
    }

    private void EnsureTravelStepInput()
    {
        if (input is null || !isInitialized || travelStepInput is { IsDisposed: false })
        {
            return;
        }

        DisposeTravelStepInput();
        travelStepInput = input.AcquireScope(
            this,
            new InputScopeOptions(
                [MajoInputActions.StepBack, MajoInputActions.StepForward],
                name: "dialogue-travel-step"));
        travelStepInput.Activated += HandleTravelStepActivated;
    }

    private void DisposeTravelStepInput()
    {
        if (travelStepInput is null)
        {
            return;
        }

        travelStepInput.Activated -= HandleTravelStepActivated;
        travelStepInput.Dispose();
        travelStepInput = null;
    }

    private void HandleTravelStepActivated(InputActivation activation)
    {
        _ = TravelFromInputAsync(activation.Action == MajoInputActions.StepForward);
    }

    private async Task TravelFromInputAsync(bool forward)
    {
        try
        {
            if (forward)
            {
                await StepForwardAsync();
            }
            else
            {
                await StepBackAsync();
            }
        }
        catch
        {
            // TravelToAsync owns structured diagnostics and failure publication.
        }
    }

    private void ClearCore(bool preserveCurrentLine = false)
    {
        lock (sync)
        {
            entries.Clear();
            currentIndex = -1;
            nextSequence = 0;
            if (!preserveCurrentLine)
            {
                currentLine = null;
            }
            lineGeneration++;
            capturedLineGeneration = -1;
            timelineGeneration++;
        }
    }

    private void PublishTimelineChanged()
    {
        DialogueTravelTimelineChanged change;
        lock (sync)
        {
            change = new DialogueTravelTimelineChanged(entries.ToArray(), currentIndex);
        }
        TimelineChanged?.Invoke(change);
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException("Dialogue travel manager is not initialized.");
        }
    }
}
