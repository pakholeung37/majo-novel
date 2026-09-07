#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Audio;

internal sealed class VoiceChannel : AudioChannelBase, IDisposable
{
    private readonly IRuntimeActivityRegistry? activities;
    private AudioPlayback? voice;
    private IDisposable? voiceActivityLease;

    public VoiceChannel(
        IAudioBackend backend,
        AudioMixer mixer,
        AudioTransitionCoordinator transitions,
        IRuntimeActivityRegistry? activities,
        IRuntimeLogger logger,
        Action<IAudioPlayback> onPlaybackStarted,
        Action<IAudioPlayback> onPlaybackStopped)
        : base(AudioCategory.Voice, backend, mixer, transitions, logger, onPlaybackStarted, onPlaybackStopped)
    {
        this.activities = activities;
    }

    public IAudioPlayback? CurrentVoice
    {
        get
        {
            lock (gate)
            {
                return voice;
            }
        }
    }

    public async Task<AudioPlaybackResult> PlayAsync(
        string clipId,
        string localizedId,
        IAudioClip clip,
        bool looping,
        float volume,
        AudioFade fade,
        CancellationToken cancellationToken)
    {
        await transitions.CompleteTransitionAsync(AudioCategory.Voice, "voice", cancellationToken);
        AudioPlayback? previous;
        lock (gate)
        {
            previous = voice;
            voice = null;
            voiceActivityLease?.Dispose();
            voiceActivityLease = null;
        }

        if (previous is not null)
        {
            logger.Info(
                "audio",
                "audio.voice_preempted",
                $"Voice '{previous.ClipId}' was preempted by '{clipId}'.");
            StopPlayback(previous);
        }

        var playback = CreatePlayback(
            localizedId, "voice", clip, looping, volume,
            fade.DurationSeconds > 0 ? 0 : mixer.CalculateEffectiveVolume(AudioCategory.Voice, volume));
        lock (gate)
        {
            voice = playback;
            voiceActivityLease = activities?.BeginActivity(
                RuntimeActivityOwner.Audio,
                "Voice",
                localizedId,
                isCancelable: true,
                isFinishable: true,
                isIgnorable: true);
        }

        var transition = transitions.StartFade(
            [new AudioFadeTarget(playback, CalculateEffectiveVolume(playback))],
            fade,
            complete: () => ApplyVolume(playback),
            cancel: () => StopPlayback(playback));
        transitions.TrackTransition(AudioCategory.Voice, "voice", transition);
        return new AudioPlaybackResult(playback, transition.Settlement);
    }

    public async Task<Task?> StopAsync(
        AudioFade fade,
        CancellationToken cancellationToken)
    {
        await transitions.CompleteTransitionAsync(AudioCategory.Voice, "voice", cancellationToken);
        AudioPlayback? current;
        lock (gate) { current = voice; }
        if (current is null)
        {
            return null;
        }

        var transition = transitions.StartFade(
            [new AudioFadeTarget(current, 0)],
            fade,
            complete: () => StopPlayback(current),
            cancel: () => ApplyVolume(current));
        transitions.TrackTransition(AudioCategory.Voice, "voice", transition);
        return transition.Settlement;
    }

    public override AudioPlayback[] GetActivePlaybacks()
    {
        lock (gate)
        {
            return voice is null ? [] : [voice];
        }
    }

    public override void StopAll()
    {
        AudioPlayback? current;
        lock (gate)
        {
            current = voice;
            voice = null;
            voiceActivityLease?.Dispose();
            voiceActivityLease = null;
        }

        if (current is not null)
        {
            StopPlayback(current);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            voiceActivityLease?.Dispose();
            voiceActivityLease = null;
        }
    }

    protected override AudioTransition? RemoveOnNaturalEnd(AudioPlayback playback)
    {
        if (voice == playback)
        {
            voice = null;
            voiceActivityLease?.Dispose();
            voiceActivityLease = null;
            return transitions.ClearOnNaturalEnd(Category, "voice");
        }
        return null;
    }

    protected override void RemoveOnStop(AudioPlayback playback)
    {
        if (voice == playback)
        {
            voice = null;
            voiceActivityLease?.Dispose();
            voiceActivityLease = null;
        }
    }
}
