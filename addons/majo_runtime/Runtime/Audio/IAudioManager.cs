#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Audio;

public interface IAudioManager : IRuntimeService
{
    event System.Action<IAudioPlayback>? PlaybackStarted;

    event System.Action<IAudioPlayback>? PlaybackStopped;

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

    Task<AudioPlaybackResult> PlayBgmAsync(
        string clipId,
        string? trackId = null,
        bool looping = true,
        float volume = 1,
        AudioFade fade = default,
        CancellationToken cancellationToken = default);

    Task<IOperationHandle> StopBgmAsync(
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

    Task<IOperationHandle> StopSfxAsync(
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

    Task<IOperationHandle?> StopVoiceAsync(
        AudioFade fade = default,
        CancellationToken cancellationToken = default);
}
