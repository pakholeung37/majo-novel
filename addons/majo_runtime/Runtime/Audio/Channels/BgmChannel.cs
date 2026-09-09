#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Audio;

internal sealed class BgmChannel : AudioChannelBase
{
    private readonly HashSet<AudioPlayback> stopping = new();
    private readonly Dictionary<string, AudioPlayback> tracks = new(StringComparer.Ordinal);

    public BgmChannel(
        IAudioBackend backend,
        AudioMixer mixer,
        AudioTransitionCoordinator transitions,
        IRuntimeLogger logger,
        Action<IAudioPlayback> onPlaybackStarted,
        Action<IAudioPlayback> onPlaybackStopped)
        : base(AudioCategory.Bgm, backend, mixer, transitions, logger, onPlaybackStarted, onPlaybackStopped)
    {
    }

    public IReadOnlyCollection<IAudioPlayback> Tracks
    {
        get
        {
            lock (gate)
            {
                return tracks.Values.Where(track => !stopping.Contains(track)).Cast<IAudioPlayback>().ToArray();
            }
        }
    }

    public IReadOnlyList<BgmRestoreTarget> CaptureCommittedTargets()
    {
        lock (gate)
        {
            return tracks.Values
                .Where(track => !stopping.Contains(track) && track.State == AudioPlaybackState.Playing)
                .OrderBy(track => track.InstanceId, StringComparer.Ordinal)
                .Select(track => new BgmRestoreTarget(
                    track.ClipId, track.InstanceId, track.IsLooping, track.BaseVolume))
                .ToArray();
        }
    }

    public AudioPlayback PreparePlayer(BgmRestoreTarget target, IAudioClip clip)
    {
        var player = backend.CreatePlayer(AudioCategory.Bgm, clip, target.TrackId);
        try
        {
            player.IsLooping = target.Loop;
            player.Volume = mixer.CalculateEffectiveVolume(AudioCategory.Bgm, target.Volume);
            return new AudioPlayback(AudioCategory.Bgm, target.ClipId, target.TrackId,
                target.Loop, target.Volume, player);
        }
        catch { player.Dispose(); throw; }
    }

    public void ReplacePrepared(IReadOnlyList<AudioPlayback> candidates)
    {
        AudioPlayback[] retired;
        lock (gate)
        {
            retired = tracks.Values.ToArray();
            tracks.Clear();
            stopping.Clear();
            foreach (var candidate in candidates) tracks.Add(candidate.InstanceId, candidate);
        }
        List<Exception>? failures = null;
        foreach (var previous in retired)
            try { StopPlayback(previous); }
            catch (Exception failure) { (failures ??= new()).Add(failure); }
        if (failures is not null) throw new AggregateException("Retired BGM cleanup failed.", failures);
        foreach (var candidate in candidates) StartPreparedPlayback(candidate);
    }

    public async Task<AudioPlaybackResult> PlayAsync(
        string clipId,
        string trackId,
        IAudioClip clip,
        bool looping,
        float volume,
        AudioFade fade,
        CancellationToken cancellationToken)
    {
        await transitions.CompleteTransitionAsync(AudioCategory.Bgm, trackId, cancellationToken);
        AudioPlayback? previous;
        lock (gate)
        {
            tracks.TryGetValue(trackId, out previous);
        }

        if (previous is not null && previous.ClipId == clipId)
        {
            previous.BaseVolume = volume;
            previous.Backend.IsLooping = looping;
            previous.IsLooping = looping;
            var modifyTransition = transitions.StartFade(
                [new AudioFadeTarget(previous, CalculateEffectiveVolume(previous))],
                fade,
                complete: () => ApplyVolume(previous),
                cancel: () =>
                {
                    ApplyVolume(previous);
                });
            transitions.TrackTransition(AudioCategory.Bgm, trackId, modifyTransition);
            return new AudioPlaybackResult(previous, modifyTransition.Settlement);
        }

        var current = CreatePlayback(
            clipId, trackId, clip, looping, volume,
            fade.DurationSeconds > 0 ? 0 : mixer.CalculateEffectiveVolume(AudioCategory.Bgm, volume));
        lock (gate)
        {
            tracks[trackId] = current;
        }

        var targets = new List<AudioFadeTarget> { new(current, CalculateEffectiveVolume(current)) };
        if (previous is not null)
        {
            targets.Add(new AudioFadeTarget(previous, 0));
        }

        var transition = transitions.StartFade(
            targets,
            fade,
            complete: () =>
            {
                if (previous is not null)
                {
                    StopPlayback(previous);
                }

                ApplyVolume(current);
            },
            cancel: () =>
            {
                if (previous is not null) StopPlayback(previous);
                ApplyVolume(current);
            });
        transitions.TrackTransition(AudioCategory.Bgm, trackId, transition);
        return new AudioPlaybackResult(current, transition.Settlement);
    }

    public async Task<Task?> StopAsync(
        string? trackId,
        AudioFade fade,
        CancellationToken cancellationToken)
    {
        var entries = SelectTracks(trackId);
        await transitions.CompleteTransitionsAsync(
            AudioCategory.Bgm,
            entries.Select(e => e.InstanceId),
            cancellationToken);
        entries = SelectTracks(trackId);
        if (entries.Length == 0)
        {
            return null;
        }

        lock (gate) stopping.UnionWith(entries);
        var transition = transitions.StartFade(
            entries.Select(entry => new AudioFadeTarget(entry, 0)).ToArray(),
            fade,
            complete: () =>
            {
                foreach (var entry in entries) StopPlayback(entry);
            },
            cancel: () =>
            {
                foreach (var entry in entries) StopPlayback(entry);
            });
        foreach (var entry in entries)
        {
            transitions.TrackTransition(AudioCategory.Bgm, entry.InstanceId, transition);
        }

        return transition.Settlement;
    }

    public AudioPlayback[] SelectTracks(string? trackId)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                return tracks.Values.ToArray();
            }

            return tracks.TryGetValue(trackId.Trim(), out var playback)
                ? [playback]
                : Array.Empty<AudioPlayback>();
        }
    }

    public override AudioPlayback[] GetActivePlaybacks()
    {
        lock (gate)
        {
            return tracks.Values.ToArray();
        }
    }

    public override void StopAll()
    {
        AudioPlayback[] snapshot;
        lock (gate)
        {
            snapshot = tracks.Values.ToArray();
            tracks.Clear();
            stopping.Clear();
        }

        foreach (var playback in snapshot)
        {
            StopPlayback(playback);
        }
    }

    protected override AudioTransition? RemoveOnNaturalEnd(AudioPlayback playback)
    {
        stopping.Remove(playback);
        if (tracks.GetValueOrDefault(playback.InstanceId) == playback)
        {
            tracks.Remove(playback.InstanceId);
            return transitions.ClearOnNaturalEnd(Category, playback.InstanceId);
        }
        return null;
    }

    protected override void RemoveOnStop(AudioPlayback playback)
    {
        stopping.Remove(playback);
        if (tracks.GetValueOrDefault(playback.InstanceId) == playback)
        {
            tracks.Remove(playback.InstanceId);
        }
    }
}
