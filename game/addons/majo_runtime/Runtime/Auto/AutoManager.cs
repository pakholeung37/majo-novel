#nullable enable

using System;
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

public sealed class AutoManager : IAutoManager
{
    private readonly object sync = new();
    private readonly IScriptPlayback script;
    private readonly IDialoguePlayer dialogue;
    private readonly IAudioManager audio;
    private readonly IFrameClock clock;
    private readonly IInputManager input;
    private readonly IHistoryManager history;
    private readonly IRuntimeLogger logger;
    private readonly TimeSpan extraDelay;
    private readonly bool waitForVoice;
    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? deadline;
    private IInputHandle? toggleInput;
    private long deadlineVersion;
    private bool isInitialized;

    public AutoManager(
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

        this.extraDelay = extraDelay;
        this.waitForVoice = waitForVoice;
    }

    public event Action<AutoModeChanged>? ModeChanged;

    public event Action<AutoPauseChanged>? PauseChanged;

    public event Action<Exception>? Failed;

    public AutoMode Mode { get; private set; } = AutoMode.Manual;

    public bool IsPaused { get; private set; }

    public TimeSpan ExtraDelay => extraDelay;

    public bool WaitForVoice => waitForVoice;

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
        Reconcile();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetMode(AutoMode.Manual);
        lock (sync)
        {
            CancelDeadlineLocked();
            SetPausedLocked(false);
        }

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
            Mode = AutoMode.Manual;
            SetPausedLocked(false);
            isInitialized = false;
        }

        return Task.CompletedTask;
    }

    public void SetMode(AutoMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        AutoModeChanged? change = null;
        lock (sync)
        {
            if (Mode == mode)
            {
                return;
            }

            change = new AutoModeChanged(Mode, mode);
            Mode = mode;
        }

        ModeChanged?.Invoke(change);
        logger.Info(
            "auto",
            "auto.mode_changed",
            $"Playback mode changed from '{change.Previous}' to '{change.Current}'.");
        Reconcile();
    }

    public void Toggle() => SetMode(
        Mode == AutoMode.Manual ? AutoMode.Auto : AutoMode.Manual);

    private void HandlePhaseChanged(ScriptPlaybackPhaseChanged _) => Reconcile();

    private void HandleHistoryOpenStateChanged(HistoryOpenStateChanged _) => Reconcile();

    private void HandleToggleInput(InputActivation _) => Toggle();

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
            new InputScopeOptions([MajoInputActions.Auto], name: "auto-toggle"));
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

    private void Reconcile()
    {
        long version = 0;
        CancellationToken token = default;
        IAudioPlayback? voice = null;
        lock (sync)
        {
            CancelDeadlineLocked();
            var paused = Mode == AutoMode.Auto &&
                (history.IsOpen || script.Phase == ScriptPlaybackPhase.PresentingOptions);
            SetPausedLocked(paused);
            if (!isInitialized ||
                Mode != AutoMode.Auto ||
                history.IsOpen ||
                script.Phase != ScriptPlaybackPhase.AwaitingAdvance)
            {
                return;
            }

            deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime!.Token);
            version = ++deadlineVersion;
            token = deadline.Token;
            voice = waitForVoice ? audio.CurrentVoice : null;
            logger.Debug(
                "auto",
                "auto.deadline_scheduled",
                $"Auto advance scheduled after {extraDelay.TotalSeconds:0.###} seconds (waitForVoice: {waitForVoice}).");
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

            await clock.DelayAsync(extraDelay, FrameTimeMode.Unscaled, cancellationToken);
            lock (sync)
            {
                if (!isInitialized ||
                    deadlineVersion != version ||
                    cancellationToken.IsCancellationRequested ||
                    Mode != AutoMode.Auto ||
                    history.IsOpen ||
                    script.Phase != ScriptPlaybackPhase.AwaitingAdvance)
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

    private void SetPausedLocked(bool paused)
    {
        if (IsPaused == paused)
        {
            return;
        }

        IsPaused = paused;
        logger.Debug(
            "auto",
            "auto.pause_changed",
            $"Auto pause state changed to '{paused}'.");
        PauseChanged?.Invoke(new AutoPauseChanged(paused));
    }
}
