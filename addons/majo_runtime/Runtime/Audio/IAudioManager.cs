#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Audio;

/// <summary>
/// Manages audio playback, volume mixing, and autonomous transitions for BGM, SFX, and Voice.
/// <para>
/// Domain Lifecycle Policy: On ResetAsync and ShutdownAsync, active audio transitions are
/// finished (applying target terminal volumes) before all playbacks are stopped and backend
/// players are disposed.
/// </para>
/// </summary>
public interface IAudioManager : IRuntimeService
{
    event Action<IAudioPlayback>? PlaybackStarted;

    event Action<IAudioPlayback>? PlaybackStopped;

    float MasterVolume { get; set; }

    float BgmVolume { get; set; }

    float SfxVolume { get; set; }

    float VoiceVolume { get; set; }

    bool MasterMuted { get; set; }

    bool BgmMuted { get; set; }

    bool SfxMuted { get; set; }

    bool VoiceMuted { get; set; }

    string VoiceLocale { get; set; }

    IAudioClipLoader BgmLoader { get; }

    IAudioClipLoader SfxLoader { get; }

    IAudioClipLoader VoiceLoader { get; }

    IReadOnlyCollection<IAudioPlayback> BgmTracks { get; }

    IReadOnlyCollection<IAudioPlayback> SfxInstances { get; }

    IAudioPlayback? CurrentVoice { get; }

    bool IsFading(string instanceId);

    void CloseSceneRequests() { }
    void OpenSceneRequests() { }


    Task<AudioPlaybackResult> PlayBgmAsync(
        string clipId,
        string? trackId = null,
        bool looping = true,
        float volume = 1,
        AudioFade fade = default,
        CancellationToken cancellationToken = default);

    Task StopBgmAsync(
        string? trackId = null,
        AudioFade fade = default,
        CancellationToken cancellationToken = default);

    Task<AudioPlaybackResult> PlaySfxAsync(
        string clipId,
        string? instanceId = null,
        bool looping = false,
        float volume = 1,
        AudioFade fade = default,
        CancellationToken cancellationToken = default);

    Task StopSfxAsync(
        string? instanceId = null,
        AudioFade fade = default,
        CancellationToken cancellationToken = default);

    Task<AudioPlaybackResult> PlayVoiceAsync(
        string clipId,
        bool looping = false,
        float volume = 1,
        AudioFade fade = default,
        string? overridePath = null,
        CancellationToken cancellationToken = default);

    Task StopVoiceAsync(
        AudioFade fade = default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finishes active transitions immediately applying their target volumes.
    /// If <paramref name="targetId"/> is null, all active transitions are finished.
    /// </summary>
    Task FinishTransitionsAsync(
        string? targetId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels active transitions immediately restoring or stopping according to cancellation policy.
    /// If <paramref name="targetId"/> is null, all active transitions are cancelled.
    /// </summary>
    Task CancelTransitionsAsync(
        string? targetId = null,
        CancellationToken cancellationToken = default);
}

