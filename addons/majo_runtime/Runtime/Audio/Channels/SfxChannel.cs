#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Audio;

internal sealed class SfxChannel : AudioChannelBase
{
    private readonly Dictionary<string, AudioPlayback> instances = new(StringComparer.Ordinal);
    private long generatedSfxId;

    public SfxChannel(
        IAudioBackend backend,
        AudioMixer mixer,
        AudioTransitionCoordinator transitions,
        IRuntimeLogger logger,
        Action<IAudioPlayback> onPlaybackStarted,
        Action<IAudioPlayback> onPlaybackStopped)
        : base(AudioCategory.Sfx, backend, mixer, transitions, logger, onPlaybackStarted, onPlaybackStopped)
    {
    }

    public IReadOnlyCollection<IAudioPlayback> Instances
    {
        get
        {
            lock (gate)
            {
                return instances.Values.Cast<IAudioPlayback>().ToArray();
            }
        }
    }

    public string ResolveInstanceId(string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId)
            ? $"sfx-{Interlocked.Increment(ref generatedSfxId)}"
            : instanceId.Trim();

    public Task<AudioPlaybackResult> PlayAsync(
        string clipId,
        string instanceId,
        IAudioClip clip,
        bool looping,
        float volume,
        AudioFade fade,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (instances.ContainsKey(instanceId))
            {
                throw new DuplicateAudioInstanceException(instanceId);
            }
        }

        var playback = CreatePlayback(
            clipId, instanceId, clip, looping, volume,
            fade.DurationSeconds > 0 ? 0 : mixer.CalculateEffectiveVolume(AudioCategory.Sfx, volume));
        lock (gate) { instances.Add(instanceId, playback); }
        var transition = transitions.StartFade(
            [new AudioFadeTarget(playback, CalculateEffectiveVolume(playback))],
            fade,
            complete: () => ApplyVolume(playback),
            cancel: () => StopPlayback(playback));
        transitions.TrackTransition(AudioCategory.Sfx, instanceId, transition);
        return Task.FromResult(new AudioPlaybackResult(playback, transition.Settlement));
    }

    public async Task<Task?> StopAsync(
        string? instanceId,
        AudioFade fade,
        CancellationToken cancellationToken)
    {
        var entries = SelectInstances(instanceId);
        await transitions.CompleteTransitionsAsync(
            AudioCategory.Sfx,
            entries.Select(e => e.InstanceId),
            cancellationToken);
        entries = SelectInstances(instanceId);
        if (entries.Length == 0)
        {
            return null;
        }

        var transition = transitions.StartFade(
            entries.Select(entry => new AudioFadeTarget(entry, 0)).ToArray(),
            fade,
            complete: () =>
            {
                foreach (var entry in entries) StopPlayback(entry);
            },
            cancel: () =>
            {
                foreach (var entry in entries) ApplyVolume(entry);
            });
        foreach (var entry in entries)
        {
            transitions.TrackTransition(AudioCategory.Sfx, entry.InstanceId, transition);
        }

        return transition.Settlement;
    }

    public AudioPlayback[] SelectInstances(string? instanceId)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return instances.Values.ToArray();
            }

            return instances.TryGetValue(instanceId.Trim(), out var playback)
                ? [playback]
                : Array.Empty<AudioPlayback>();
        }
    }

    public override AudioPlayback[] GetActivePlaybacks()
    {
        lock (gate)
        {
            return instances.Values.ToArray();
        }
    }

    public override void StopAll()
    {
        AudioPlayback[] snapshot;
        lock (gate)
        {
            snapshot = instances.Values.ToArray();
            instances.Clear();
        }

        foreach (var playback in snapshot)
        {
            StopPlayback(playback);
        }
    }

    public void Reset()
    {
        StopAll();
        generatedSfxId = 0;
    }

    protected override AudioTransition? RemoveOnNaturalEnd(AudioPlayback playback)
    {
        if (instances.GetValueOrDefault(playback.InstanceId) == playback)
        {
            instances.Remove(playback.InstanceId);
            return transitions.ClearOnNaturalEnd(Category, playback.InstanceId);
        }
        return null;
    }

    protected override void RemoveOnStop(AudioPlayback playback)
    {
        if (instances.GetValueOrDefault(playback.InstanceId) == playback)
        {
            instances.Remove(playback.InstanceId);
        }
    }
}
