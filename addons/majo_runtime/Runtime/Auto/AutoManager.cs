#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Auto;

public sealed class AutoManager : IAutoManager
{
    private readonly IPlaybackModeManager playback;
    private bool isInitialized;

    public AutoManager(IPlaybackModeManager playback)
    {
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public event Action<AutoModeChanged>? ModeChanged;
    public event Action<AutoPauseChanged>? PauseChanged;
    public event Action<Exception>? Failed;

    public AutoMode Mode => playback.Mode == PlaybackMode.Auto ? AutoMode.Auto : AutoMode.Manual;
    public bool IsPaused => playback.IsPaused;
    public TimeSpan ExtraDelay => playback.ExtraDelay;
    public bool WaitForVoice => playback.WaitForVoice;
    public bool HasPendingDeadline => playback.HasPendingDeadline;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }
        playback.ModeChanged += HandleModeChanged;
        playback.PauseChanged += HandlePauseChanged;
        playback.Failed += HandleFailed;
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        playback.SetMode(PlaybackMode.Manual);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }
        playback.ModeChanged -= HandleModeChanged;
        playback.PauseChanged -= HandlePauseChanged;
        playback.Failed -= HandleFailed;
        isInitialized = false;
        return Task.CompletedTask;
    }

    public void SetMode(AutoMode mode) => playback.SetMode(mode switch
    {
        AutoMode.Manual => PlaybackMode.Manual,
        AutoMode.Auto => PlaybackMode.Auto,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    });

    public void Toggle() => playback.ToggleAuto();

    private void HandleModeChanged(PlaybackModeChanged change) => ModeChanged?.Invoke(
        new AutoModeChanged(ToAutoMode(change.Previous), ToAutoMode(change.Current)));

    private void HandlePauseChanged(PlaybackPauseStateChanged change) =>
        PauseChanged?.Invoke(new AutoPauseChanged(change.IsPaused));

    private void HandleFailed(Exception exception) => Failed?.Invoke(exception);

    private static AutoMode ToAutoMode(PlaybackMode mode) =>
        mode == PlaybackMode.Auto ? AutoMode.Auto : AutoMode.Manual;
}
