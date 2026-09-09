#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Input;
using Majo.Runtime.State;

namespace Majo.Runtime.Travel;

public sealed class DialogueTravelManager : IDialogueTravelManager, IDisposable
{
    private readonly object sync = new();
    private readonly List<DialogueTravelEntry> entries = new();
    private readonly IGameSessionCoordinator sessions;
    private readonly IStateBoundarySource boundaries;
    private readonly IInputManager? input;
    private readonly IRuntimeLogger logger;
    private IInputHandle? travelStepInput;
    private int currentIndex = -1;
    private long nextSequence;
    private bool isTravelling;
    private bool isInitialized;

    public DialogueTravelManager(
        IStateManager state,
        IGameSessionCoordinator sessions,
        int capacity,
        IInputManager? input = null,
        IRuntimeLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        boundaries = state as IStateBoundarySource ?? throw new ArgumentException(
            "Travel requires an explicit line boundary source.", nameof(state));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.input = input;
        this.logger = logger ?? NullRuntimeLogger.Instance;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);

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

        boundaries.BoundaryCommitted += RegisterCheckpoint;
        sessions.SessionChanged += HandleSessionChanged;
        isInitialized = true;
        EnsureTravelStepInput();

        if (boundaries.CurrentBoundary is { } boundary) RegisterCheckpoint(boundary);
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearCore();
        EnsureTravelStepInput();
        PublishTimelineChanged();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dispose();
        return Task.CompletedTask;
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
        cancellationToken.ThrowIfCancellationRequested();
        DialogueTravelEntry? target = null;
        var targetIndex = -1;
        var previousIndex = -1;
        var ownsTravel = false;
        try
        {
            lock (sync)
            {
                if (isTravelling) return false;
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
                isTravelling = ownsTravel = true;
            }

            await sessions.RollbackAsync(target.Snapshot, cancellationToken);

            lock (sync)
            {
                var restoredIndex = entries.FindIndex(entry => ReferenceEquals(entry, target));
                // A reset/load may have replaced the timeline while State was awaited.
                if (!isInitialized || restoredIndex < 0) return false;
                currentIndex = restoredIndex;
                // Replay owns a new playback generation. Subsequent UI revisions belong
                // to this historical entry, while the next normal line is a new occurrence.
                if (boundaries.CurrentBoundary is { } restored && restored.Context.Script.Spot == target.Spot)
                {
                    target = target with
                    {
                        SessionGeneration = restored.Context.SessionGeneration,
                        OccurrenceSequence = restored.Context.OccurrenceSequence,
                    };
                    entries[restoredIndex] = target;
                }
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
                if (ownsTravel) isTravelling = false;
            }
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

    private void HandleSessionChanged(GameSessionChanged change)
    {
        if (!change.BeganNewSession) return;
        var boundary = boundaries.CurrentBoundary;
        ClearCore();
        if (change.Transition == GameSessionTransition.LoadGame && boundary is not null)
        {
            RegisterCheckpoint(boundary, allowRestore: true);
        }
        else PublishTimelineChanged();
    }

    public void RegisterCheckpoint(CommittedLineBoundary boundary) =>
        RegisterCheckpoint(boundary, allowRestore: false);

    private void RegisterCheckpoint(CommittedLineBoundary boundary, bool allowRestore)
    {
        var line = boundary.Context.Line;
        var spot = boundary.Context.Script.Spot;
        var snapshot = boundary.Snapshot;
        lock (sync)
        {
            if (!isInitialized || isTravelling) return;
            var replacesReplay = line.Origin == DialoguePresentationOrigin.HotReloadReplay &&
                currentIndex >= 0 && entries[currentIndex].Spot == spot;
            var replacesRevision = currentIndex >= 0 &&
                entries[currentIndex].SessionGeneration == boundary.Context.SessionGeneration &&
                entries[currentIndex].OccurrenceSequence == boundary.Context.OccurrenceSequence &&
                boundary.Context.OccurrenceSequence != 0;
            if (!allowRestore && line.Origin == DialoguePresentationOrigin.CheckpointRestore && !replacesRevision) return;
            var replacesCurrent = replacesReplay || replacesRevision;
            var sequence = replacesCurrent ? entries[currentIndex].Sequence : ++nextSequence;
            var entry = new DialogueTravelEntry(sequence, spot, snapshot,
                line.Speaker, line.Text.Text, snapshot.CapturedAt,
                boundary.Context.OccurrenceSequence, boundary.Context.SessionGeneration);
            if (currentIndex >= 0 && currentIndex < entries.Count - 1)
                entries.RemoveRange(currentIndex + 1, entries.Count - currentIndex - 1);
            if (replacesCurrent) entries[currentIndex] = entry;
            else
            {
                entries.Add(entry);
                if (entries.Count > Capacity) entries.RemoveRange(0, entries.Count - Capacity);
                currentIndex = entries.Count - 1;
            }
        }
        logger.Debug("travel", "travel.checkpoint_registered",
            $"Registered occurrence {boundary.Context.OccurrenceSequence} at '{spot.LineId}'.");
        PublishTimelineChanged();
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

    private void ClearCore()
    {
        lock (sync)
        {
            entries.Clear();
            currentIndex = -1;
            nextSequence = 0;
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

    public void Dispose()
    {
        boundaries.BoundaryCommitted -= RegisterCheckpoint;
        sessions.SessionChanged -= HandleSessionChanged;
        DisposeTravelStepInput();
        isInitialized = false;
        ClearCore();
    }
}
