#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.Auto;

public enum PlaybackMode
{
    Manual,
    Auto,
    SkipReadOnly,
    SkipAll,
}

public enum PlaybackPauseReason
{
    History,
    Options,
    Modal,
    StateRestore,
    HotReload,
    External,
}

public sealed record PlaybackModeChanged(PlaybackMode Previous, PlaybackMode Current);

public sealed record PlaybackPauseStateChanged(
    bool IsPaused,
    IReadOnlyList<PlaybackPauseReason> Reasons);

public interface IPlaybackPauseHandle : IDisposable
{
    PlaybackPauseReason Reason { get; }
    bool IsDisposed { get; }
}

public interface IPlaybackModeManager : IRuntimeService
{
    event Action<PlaybackModeChanged>? ModeChanged;
    event Action<PlaybackPauseStateChanged>? PauseChanged;
    event Action<Exception>? Failed;

    PlaybackMode Mode { get; }
    bool IsPaused { get; }
    IReadOnlyList<PlaybackPauseReason> PauseReasons { get; }
    TimeSpan ExtraDelay { get; }
    bool WaitForVoice { get; }
    TimeSpan SkipDelay => TimeSpan.Zero;
    bool HasPendingDeadline { get; }

    void SetMode(PlaybackMode mode);
    void ToggleAuto();
    void ApplyAutoSettings(TimeSpan extraDelay, bool waitForVoice)
    {
    }
    void ApplySkipSettings(TimeSpan delay, PlaybackMode defaultMode)
    {
    }
    void ToggleSkip()
    {
    }
    IPlaybackPauseHandle AcquirePause(PlaybackPauseReason reason);
}
