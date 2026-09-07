#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Audio;

internal abstract class AudioChannelBase
{
    protected readonly object gate = new();
    protected readonly IAudioBackend backend;
    protected readonly AudioMixer mixer;
    protected readonly AudioTransitionCoordinator transitions;
    protected readonly IRuntimeLogger logger;
    private readonly Action<IAudioPlayback> onPlaybackStarted;
    private readonly Action<IAudioPlayback> onPlaybackStopped;

    protected AudioChannelBase(
        AudioCategory category,
        IAudioBackend backend,
        AudioMixer mixer,
        AudioTransitionCoordinator transitions,
        IRuntimeLogger logger,
        Action<IAudioPlayback> onPlaybackStarted,
        Action<IAudioPlayback> onPlaybackStopped)
    {
        Category = category;
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        this.transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.onPlaybackStarted = onPlaybackStarted ?? throw new ArgumentNullException(nameof(onPlaybackStarted));
        this.onPlaybackStopped = onPlaybackStopped ?? throw new ArgumentNullException(nameof(onPlaybackStopped));
    }

    public AudioCategory Category { get; }

    public void ApplyVolume(AudioPlayback playback) =>
        playback.Backend.Volume = CalculateEffectiveVolume(playback);

    public float CalculateEffectiveVolume(AudioPlayback playback) =>
        mixer.CalculateEffectiveVolume(Category, playback.BaseVolume);

    public virtual void ApplyVolumes()
    {
        var categoryTransitions = transitions.FinishCategoryTransitions(Category);
        var snapshot = GetActivePlaybacks();
        foreach (var playback in snapshot)
        {
            ApplyVolume(playback);
        }

        if (categoryTransitions.Length > 0)
        {
            _ = ReapplyCategoryAfterTransitionsAsync(categoryTransitions);
        }
    }

    public abstract AudioPlayback[] GetActivePlaybacks();

    public abstract void StopAll();

    protected abstract AudioTransition? RemoveOnNaturalEnd(AudioPlayback playback);

    protected abstract void RemoveOnStop(AudioPlayback playback);

    protected AudioPlayback CreatePlayback(
        string clipId,
        string instanceId,
        IAudioClip clip,
        bool looping,
        float baseVolume,
        float initialVolume)
    {
        var backendPlayer = backend.CreatePlayer(Category, clip, instanceId);
        backendPlayer.IsLooping = looping;
        backendPlayer.Volume = initialVolume;
        var playback = new AudioPlayback(
            Category, clipId, instanceId, looping, baseVolume, backendPlayer);
        backendPlayer.Play();
        logger.Debug(
            "audio",
            $"audio.{Category.ToString().ToLowerInvariant()}_started",
            $"{Category} playback '{clipId}' started as '{instanceId}'.");
        onPlaybackStarted(playback);
        _ = ObservePlaybackAsync(playback);
        return playback;
    }

    protected void StopPlayback(AudioPlayback playback)
    {
        lock (gate)
        {
            if (playback.State != AudioPlaybackState.Playing)
            {
                return;
            }

            playback.State = AudioPlaybackState.Stopped;
            RemoveOnStop(playback);
        }

        playback.Backend.StopPlayback();
        playback.Backend.Dispose();
        playback.Complete();
        logger.Debug(
            "audio",
            $"audio.{Category.ToString().ToLowerInvariant()}_stopped",
            $"{Category} playback '{playback.ClipId}' stopped.");
    }

    private async Task ObservePlaybackAsync(AudioPlayback playback)
    {
        try
        {
            await playback.Backend.Completion;
        }
        catch (Exception exception)
        {
            playback.Fail(exception);
            logger.Error(
                "audio",
                "audio.playback_failed",
                $"{playback.Category} playback '{playback.ClipId}' failed.",
                exception);
            return;
        }

        AudioTransition? activeTransition = null;
        lock (gate)
        {
            if (playback.State == AudioPlaybackState.Playing)
            {
                playback.State = AudioPlaybackState.Finished;
            }

            activeTransition = RemoveOnNaturalEnd(playback);
        }

        activeTransition?.Finish();
        playback.Complete();
        logger.Debug(
            "audio",
            $"audio.{playback.Category.ToString().ToLowerInvariant()}_finished",
            $"{playback.Category} playback '{playback.ClipId}' reached its terminal state.");
        onPlaybackStopped(playback);
    }

    private async Task ReapplyCategoryAfterTransitionsAsync(
        IReadOnlyCollection<AudioTransition> categoryTransitions)
    {
        foreach (var transition in categoryTransitions)
        {
            try
            {
                await transition.Settlement;
            }
            catch
            {
            }
        }

        var snapshot = GetActivePlaybacks();
        foreach (var playback in snapshot)
        {
            ApplyVolume(playback);
        }
    }
}
