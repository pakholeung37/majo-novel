#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Script;

namespace Majo.Runtime.Auto;

public sealed class PlaybackModeManager : IPlaybackModeManager
{
    private readonly object sync = new();
    private readonly IScriptPlayback script;
    private readonly IDialoguePlayer dialogue;
    private readonly IAudioManager audio;
    private readonly IFrameClock clock;
    private readonly IInputManager input;
    private readonly IHistoryManager history;
    private readonly IRuntimeLogger logger;
    private readonly Dictionary<PlaybackPauseReason, int> pauseCounts = new();
    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? deadline;
    private IInputHandle? toggleInput;
    private long deadlineVersion;
    private bool isInitialized;

    public PlaybackModeManager(
        IScriptPlayback script,
        IDialoguePlayer dialogue,
        IAudioManager audio,
        IFrameClock clock,
        IInputManager input,
        IHistoryManager history,
        TimeSpan extraDelay,
        bool waitForVoice,
        IRuntimeLogger? logger = null)
    {
        this.script = script ?? throw new ArgumentNullException(nameof(script));
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.logger = logger ?? NullRuntimeLogger.Instance;
        if (extraDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extraDelay));
        }

        ExtraDelay = extraDelay;
        WaitForVoice = waitForVoice;
    }

    public event Action<PlaybackModeChanged>? ModeChanged;
    public event Action<PlaybackPauseStateChanged>? PauseChanged;
    public event Action<Exception>? Failed;

    public PlaybackMode Mode { get; private set; } = PlaybackMode.Manual;
    public bool IsPaused => PauseReasons.Count > 0;
    public IReadOnlyList<PlaybackPauseReason> PauseReasons
    {
        get
        {
            lock (sync)
            {
                return pauseCounts.Keys.OrderBy(value => value).ToArray();
            }
        }
    }
    public TimeSpan ExtraDelay { get; }
    public bool WaitForVoice { get; }
    public bool HasPendingDeadline
    {
        get { lock (sync) { return deadline is not null; } }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        lifetime = new CancellationTokenSource();
        script.PhaseChanged += HandlePhaseChanged;
        history.OpenStateChanged += HandleHistoryOpenStateChanged;
        input.Dispatched += HandleInputDispatched;
        EnsureToggleInput();
        isInitialized = true;
        ReconcileIntrinsicPauses();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetMode(PlaybackMode.Manual);
        lock (sync)
        {
            CancelDeadlineLocked();
            pauseCounts.Clear();
        }
        PublishPauseState();
        EnsureToggleInput();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }

        script.PhaseChanged -= HandlePhaseChanged;
        history.OpenStateChanged -= HandleHistoryOpenStateChanged;
        input.Dispatched -= HandleInputDispatched;
        if (toggleInput is not null)
        {
            toggleInput.Activated -= HandleToggleInput;
            toggleInput.Dispose();
            toggleInput = null;
        }

        lock (sync)
        {
            CancelDeadlineLocked();
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
            Mode = PlaybackMode.Manual;
            pauseCounts.Clear();
            isInitialized = false;
        }
        return Task.CompletedTask;
    }

    public void SetMode(PlaybackMode mode)
    {
        if (mode is PlaybackMode.SkipReadOnly or PlaybackMode.SkipAll)
        {
            throw new NotSupportedException("Skip playback modes are introduced in M15.");
        }
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        PlaybackModeChanged? change;
        lock (sync)
        {
            if (Mode == mode)
            {
                return;
            }
            change = new PlaybackModeChanged(Mode, mode);
            Mode = mode;
        }

        ModeChanged?.Invoke(change);
        logger.Info(
            "auto",
            "playback.mode_changed",
            $"Playback mode changed from '{change.Previous}' to '{change.Current}'.");
        ReconcileDeadline();
    }

    public void ToggleAuto() => SetMode(
        Mode == PlaybackMode.Auto ? PlaybackMode.Manual : PlaybackMode.Auto);

    public IPlaybackPauseHandle AcquirePause(PlaybackPauseReason reason)
    {
        if (reason is PlaybackPauseReason.History or PlaybackPauseReason.Options)
        {
            throw new ArgumentException(
                $"Pause reason '{reason}' is owned by PlaybackModeManager.",
                nameof(reason));
        }
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        lock (sync)
        {
            pauseCounts.TryGetValue(reason, out var count);
            pauseCounts[reason] = count + 1;
        }
        PublishPauseState();
        ReconcileDeadline();
        return new PauseHandle(this, reason);
    }

    private void HandlePhaseChanged(ScriptPlaybackPhaseChanged _) => ReconcileIntrinsicPauses();
    private void HandleHistoryOpenStateChanged(HistoryOpenStateChanged _) => ReconcileIntrinsicPauses();
    private void HandleToggleInput(InputActivation _) => ToggleAuto();

    private void EnsureToggleInput()
    {
        if (toggleInput is { IsDisposed: false })
        {
            return;
        }
        if (toggleInput is not null)
        {
            toggleInput.Activated -= HandleToggleInput;
        }
        toggleInput = input.AcquireScope(
            this,
            new InputScopeOptions([MajoInputActions.Auto], name: "playback-auto-toggle"));
        toggleInput.Activated += HandleToggleInput;
    }

    private void HandleInputDispatched(InputActivation activation)
    {
        if (activation.Action != MajoInputActions.Advance)
        {
            return;
        }
        lock (sync)
        {
            CancelDeadlineLocked();
        }
    }

    private void ReconcileIntrinsicPauses()
    {
        var changed = false;
        lock (sync)
        {
            changed |= SetIntrinsicPauseLocked(PlaybackPauseReason.History, history.IsOpen);
            changed |= SetIntrinsicPauseLocked(
                PlaybackPauseReason.Options,
                script.Phase == ScriptPlaybackPhase.PresentingOptions);
        }
        if (changed)
        {
            PublishPauseState();
        }
        ReconcileDeadline();
    }

    private bool SetIntrinsicPauseLocked(PlaybackPauseReason reason, bool active)
    {
        var exists = pauseCounts.ContainsKey(reason);
        if (active == exists)
        {
            return false;
        }
        if (active)
        {
            pauseCounts[reason] = 1;
        }
        else
        {
            pauseCounts.Remove(reason);
        }
        return true;
    }

    private void ReconcileDeadline()
    {
        long version = 0;
        CancellationToken token = default;
        IAudioPlayback? voice = null;
        lock (sync)
        {
            CancelDeadlineLocked();
            if (!isInitialized || Mode != PlaybackMode.Auto || pauseCounts.Count > 0 ||
                script.Phase != ScriptPlaybackPhase.AwaitingAdvance)
            {
                return;
            }

            deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime!.Token);
            version = ++deadlineVersion;
            token = deadline.Token;
            voice = WaitForVoice ? audio.CurrentVoice : null;
            logger.Debug(
                "auto",
                "auto.deadline_scheduled",
                $"Auto advance scheduled after {ExtraDelay.TotalSeconds:0.###} seconds " +
                $"(waitForVoice: {WaitForVoice}).");
        }
        _ = RunDeadlineAsync(version, voice, token);
    }

    private async Task RunDeadlineAsync(
        long version,
        IAudioPlayback? voice,
        CancellationToken cancellationToken)
    {
        try
        {
            if (voice is not null && voice.State == AudioPlaybackState.Playing)
            {
                await voice.Completion.WaitAsync(cancellationToken);
            }
            await clock.DelayAsync(ExtraDelay, FrameTimeMode.Unscaled, cancellationToken);
            lock (sync)
            {
                if (!isInitialized || deadlineVersion != version ||
                    cancellationToken.IsCancellationRequested || Mode != PlaybackMode.Auto ||
                    pauseCounts.Count > 0 || script.Phase != ScriptPlaybackPhase.AwaitingAdvance)
                {
                    return;
                }
                CancelDeadlineLocked();
                dialogue.RequestNextLine();
                logger.Debug(
                    "auto",
                    "auto.deadline_elapsed",
                    "Auto advance deadline elapsed; requested the next line.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                CancelDeadlineLocked();
            }
            logger.Error(
                "auto",
                "auto.deadline_failed",
                $"Auto advance failed: {exception.Message}",
                exception);
            Failed?.Invoke(exception);
        }
    }

    private void ReleasePause(PlaybackPauseReason reason)
    {
        lock (sync)
        {
            if (!pauseCounts.TryGetValue(reason, out var count))
            {
                return;
            }
            if (count <= 1)
            {
                pauseCounts.Remove(reason);
            }
            else
            {
                pauseCounts[reason] = count - 1;
            }
        }
        PublishPauseState();
        ReconcileDeadline();
    }

    private void PublishPauseState()
    {
        var reasons = PauseReasons;
        logger.Debug(
            "auto",
            "playback.pause_changed",
            $"Playback pause reasons: {(reasons.Count == 0 ? "<none>" : string.Join(", ", reasons))}.");
        PauseChanged?.Invoke(new PlaybackPauseStateChanged(reasons.Count > 0, reasons));
    }

    private void CancelDeadlineLocked()
    {
        deadlineVersion++;
        if (deadline is null)
        {
            return;
        }
        deadline.Cancel();
        deadline.Dispose();
        deadline = null;
    }

    private sealed class PauseHandle : IPlaybackPauseHandle
    {
        private PlaybackModeManager? owner;

        public PauseHandle(PlaybackModeManager owner, PlaybackPauseReason reason)
        {
            this.owner = owner;
            Reason = reason;
        }

        public PlaybackPauseReason Reason { get; }
        public bool IsDisposed => owner is null;

        public void Dispose()
        {
            var manager = Interlocked.Exchange(ref owner, null);
            manager?.ReleasePause(Reason);
        }
    }
}
