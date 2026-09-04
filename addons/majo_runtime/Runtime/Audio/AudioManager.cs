#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Audio;

public sealed class AudioManager : IAudioManager, IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly IAudioBackend backend;
    private readonly Dictionary<string, AudioPlayback> bgm = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioPlayback> sfx = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioTransition> bgmTransitions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioTransition> sfxTransitions =
        new(StringComparer.Ordinal);
    private readonly IRuntimeLogger logger;
    private readonly IMissingResourceReporter? missingResources;
    private readonly bool failOnMissingClips;
    private readonly ILocaleManager? locales;
    private readonly ILocalizedResourceResolver? localizedResources;
    private readonly IRuntimeActivityRegistry? activities;
    private AudioPlayback? voice;
    private IDisposable? voiceActivityLease;
    private AudioTransition? voiceTransition;
    private long generatedSfxId;
    private bool isInitialized;
    private bool backendInitializationAttempted;
    private float masterVolume;
    private float bgmVolume;
    private float sfxVolume;
    private float voiceVolume;
    private bool masterMuted;
    private bool bgmMuted;
    private bool sfxMuted;
    private bool voiceMuted;
    private string voiceLocale;

    public AudioManager(
        IAudioBackend backend,
        IAudioClipLoader bgmLoader,
        IAudioClipLoader sfxLoader,
        IAudioClipLoader voiceLoader,
        AudioSettings settings,
        IRuntimeLogger? logger = null,
        IMissingResourceReporter? missingResources = null,
        ILocaleManager? locales = null,
        ILocalizedResourceResolver? localizedResources = null,
        IRuntimeActivityRegistry? activities = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        BgmLoader = bgmLoader ?? throw new ArgumentNullException(nameof(bgmLoader));
        SfxLoader = sfxLoader ?? throw new ArgumentNullException(nameof(sfxLoader));
        VoiceLoader = voiceLoader ?? throw new ArgumentNullException(nameof(voiceLoader));
        this.logger = logger ?? NullRuntimeLogger.Instance;
        this.missingResources = missingResources;
        this.locales = locales;
        this.localizedResources = localizedResources;
        this.activities = activities;
        ArgumentNullException.ThrowIfNull(settings);
        masterVolume = ValidateVolume(settings.MasterVolume);
        bgmVolume = ValidateVolume(settings.BgmVolume);
        sfxVolume = ValidateVolume(settings.SfxVolume);
        voiceVolume = ValidateVolume(settings.VoiceVolume);
        masterMuted = settings.MasterMuted;
        bgmMuted = settings.BgmMuted;
        sfxMuted = settings.SfxMuted;
        voiceMuted = settings.VoiceMuted;
        voiceLocale = RequireName(settings.VoiceLocale, nameof(settings.VoiceLocale));
        failOnMissingClips = settings.FailOnMissingClips;
    }

    public event Action<IAudioPlayback>? PlaybackStarted;

    public event Action<IAudioPlayback>? PlaybackStopped;

    public float MasterVolume
    {
        get => masterVolume;
        set { masterVolume = ValidateVolume(value); ApplyAllVolumes(); }
    }

    public float BgmVolume
    {
        get => bgmVolume;
        set { bgmVolume = ValidateVolume(value); ApplyCategoryVolumes(AudioCategory.Bgm); }
    }

    public float SfxVolume
    {
        get => sfxVolume;
        set { sfxVolume = ValidateVolume(value); ApplyCategoryVolumes(AudioCategory.Sfx); }
    }

    public float VoiceVolume
    {
        get => voiceVolume;
        set { voiceVolume = ValidateVolume(value); ApplyCategoryVolumes(AudioCategory.Voice); }
    }

    public bool MasterMuted
    {
        get => masterMuted;
        set { masterMuted = value; ApplyAllVolumes(); }
    }

    public bool BgmMuted
    {
        get => bgmMuted;
        set { bgmMuted = value; ApplyCategoryVolumes(AudioCategory.Bgm); }
    }

    public bool SfxMuted
    {
        get => sfxMuted;
        set { sfxMuted = value; ApplyCategoryVolumes(AudioCategory.Sfx); }
    }

    public bool VoiceMuted
    {
        get => voiceMuted;
        set { voiceMuted = value; ApplyCategoryVolumes(AudioCategory.Voice); }
    }

    public string VoiceLocale
    {
        get => voiceLocale;
        set => voiceLocale = RequireName(value, nameof(value));
    }

    public IAudioClipLoader BgmLoader { get; }

    public IAudioClipLoader SfxLoader { get; }

    public IAudioClipLoader VoiceLoader { get; }

    public IReadOnlyCollection<IAudioPlayback> BgmTracks
    {
        get { lock (gate) { return bgm.Values.Cast<IAudioPlayback>().ToArray(); } }
    }

    public IReadOnlyCollection<IAudioPlayback> SfxInstances
    {
        get { lock (gate) { return sfx.Values.Cast<IAudioPlayback>().ToArray(); } }
    }

    public IAudioPlayback? CurrentVoice
    {
        get { lock (gate) { return voice; } }
    }

    public bool IsFading(string instanceId)
    {
        lock (gate)
        {
            if (bgmTransitions.TryGetValue(instanceId, out var bgm) && !bgm.Settlement.IsCompleted)
            {
                return true;
            }
            if (sfxTransitions.TryGetValue(instanceId, out var sfx) && !sfx.Settlement.IsCompleted)
            {
                return true;
            }
            if (instanceId == "voice" && voiceTransition is not null && !voiceTransition.Settlement.IsCompleted)
            {
                return true;
            }
            return false;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (isInitialized)
        {
            return;
        }

        backendInitializationAttempted = true;
        await backend.InitializeAsync(cancellationToken);
        if (locales is not null)
        {
            VoiceLocale = locales.VoiceLocale;
            locales.VoiceLocaleChanged += HandleVoiceLocaleChanged;
        }
        isInitialized = true;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await mutations.WaitAsync(cancellationToken);
        try
        {
            await CompleteTransitionsAsync(cancellationToken);
            StopEveryPlayback();
            generatedSfxId = 0;
            await backend.ResetAsync(cancellationToken);
        }
        finally
        {
            mutations.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized && !backendInitializationAttempted)
        {
            return;
        }

        await mutations.WaitAsync(cancellationToken);
        try
        {
            await CompleteTransitionsAsync(cancellationToken);
            StopEveryPlayback();
            await backend.ShutdownAsync(cancellationToken);
            if (locales is not null)
            {
                locales.VoiceLocaleChanged -= HandleVoiceLocaleChanged;
            }
            isInitialized = false;
            backendInitializationAttempted = false;
        }
        finally
        {
            mutations.Release();
        }
    }

    public async Task<AudioPlaybackResult> PlayBgmAsync(
        string clipId,
        string? trackId = null,
        bool looping = true,
        float volume = 1,
        AudioFade fade = default,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        clipId = RequireName(clipId, nameof(clipId));
        trackId = string.IsNullOrWhiteSpace(trackId) ? clipId : trackId.Trim();
        volume = ValidateVolume(volume);
        fade = fade.Validate();
        if (ShouldSkipMissing(BgmLoader, clipId))
        {
            ReportMissingAudio(AudioCategory.Bgm, clipId, BgmLoader);
            return AudioPlaybackResult.Skipped(AudioCategory.Bgm, clipId, trackId);
        }

        var clip = await BgmLoader.LoadAsync(clipId, cancellationToken: cancellationToken);

        await mutations.WaitAsync(cancellationToken);
        try
        {
            await CompleteTransitionAsync(bgmTransitions, trackId, cancellationToken);
            bgm.TryGetValue(trackId, out var previous);
            if (previous is not null && previous.ClipId == clipId)
            {
                var previousVolume = previous.BaseVolume;
                var previousLoop = previous.Backend.IsLooping;
                previous.BaseVolume = volume;
                previous.Backend.IsLooping = looping;
                previous.IsLooping = looping;
                var modifyTransition = StartFade(
                    [new AudioFadeTarget(previous, EffectiveVolume(previous))],
                    fade,
                    complete: () => ApplyVolume(previous),
                    cancel: () =>
                    {
                        previous.BaseVolume = previousVolume;
                        previous.Backend.IsLooping = previousLoop;
                        previous.IsLooping = previousLoop;
                        ApplyVolume(previous);
                    });
                TrackTransition(bgmTransitions, trackId, modifyTransition);
                return new AudioPlaybackResult(previous, modifyTransition.Settlement);
            }

            var current = CreatePlayback(
                AudioCategory.Bgm, clipId, trackId, clip, looping, volume,
                fade.DurationSeconds > 0 ? 0 : EffectiveVolume(AudioCategory.Bgm, volume));
            lock (gate)
            {
                bgm[trackId] = current;
            }

            var targets = new List<AudioFadeTarget> { new(current, EffectiveVolume(current)) };
            if (previous is not null)
            {
                targets.Add(new AudioFadeTarget(previous, 0));
            }

            var transition = StartFade(
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
                    StopPlayback(current);
                    if (previous is not null)
                    {
                        lock (gate) { bgm[trackId] = previous; }
                        ApplyVolume(previous);
                    }
                    else
                    {
                        lock (gate) { bgm.Remove(trackId); }
                    }
                });
            TrackTransition(bgmTransitions, trackId, transition);
            return new AudioPlaybackResult(current, transition.Settlement);
        }
        finally
        {
            mutations.Release();
        }
    }

    public async Task StopBgmAsync(
        string? trackId = null,
        AudioFade fade = default,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        fade = fade.Validate();
        AudioTransition? transition;
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var entries = SelectBgm(trackId);
            await CompleteTransitionsAsync(
                GetTransitions(bgmTransitions, entries),
                cancellationToken);
            entries = SelectBgm(trackId);
            if (entries.Length == 0)
            {
                return;
            }

            transition = StartFade(
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
                TrackTransition(bgmTransitions, entry.InstanceId, transition);
            }
        }
        finally
        {
            mutations.Release();
        }

        if (transition is not null)
        {
            await transition.Settlement.WaitAsync(cancellationToken);
        }
    }

    public async Task<AudioPlaybackResult> PlaySfxAsync(
        string clipId,
        string? instanceId = null,
        bool looping = false,
        float volume = 1,
        AudioFade fade = default,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        clipId = RequireName(clipId, nameof(clipId));
        volume = ValidateVolume(volume);
        fade = fade.Validate();
        instanceId = string.IsNullOrWhiteSpace(instanceId)
            ? $"sfx-{Interlocked.Increment(ref generatedSfxId)}"
            : instanceId.Trim();
        if (ShouldSkipMissing(SfxLoader, clipId))
        {
            ReportMissingAudio(AudioCategory.Sfx, clipId, SfxLoader);
            return AudioPlaybackResult.Skipped(AudioCategory.Sfx, clipId, instanceId);
        }

        var clip = await SfxLoader.LoadAsync(clipId, cancellationToken: cancellationToken);
        await mutations.WaitAsync(cancellationToken);
        try
        {
            lock (gate)
            {
                if (sfx.ContainsKey(instanceId))
                {
                    throw new DuplicateAudioInstanceException(instanceId);
                }
            }

            var playback = CreatePlayback(
                AudioCategory.Sfx, clipId, instanceId, clip, looping, volume,
                fade.DurationSeconds > 0 ? 0 : EffectiveVolume(AudioCategory.Sfx, volume));
            lock (gate) { sfx.Add(instanceId, playback); }
            var transition = StartFade(
                [new AudioFadeTarget(playback, EffectiveVolume(playback))],
                fade,
                complete: () => ApplyVolume(playback),
                cancel: () => StopPlayback(playback));
            TrackTransition(sfxTransitions, instanceId, transition);
            return new AudioPlaybackResult(playback, transition.Settlement);
        }
        finally
        {
            mutations.Release();
        }
    }

    public async Task StopSfxAsync(
        string? instanceId = null,
        AudioFade fade = default,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        fade = fade.Validate();
        AudioTransition? transition;
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var entries = SelectSfx(instanceId);
            await CompleteTransitionsAsync(
                GetTransitions(sfxTransitions, entries),
                cancellationToken);
            entries = SelectSfx(instanceId);
            if (entries.Length == 0)
            {
                return;
            }

            transition = StartFade(
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
                TrackTransition(sfxTransitions, entry.InstanceId, transition);
            }
        }
        finally
        {
            mutations.Release();
        }

        if (transition is not null)
        {
            await transition.Settlement.WaitAsync(cancellationToken);
        }
    }

    public async Task<AudioPlaybackResult> PlayVoiceAsync(
        string clipId,
        bool looping = false,
        float volume = 1,
        AudioFade fade = default,
        string? overridePath = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        clipId = RequireName(clipId, nameof(clipId));
        volume = ValidateVolume(volume);
        fade = fade.Validate();
        var localizedId = ResolveLocalizedVoiceClipId(clipId);
        if (ShouldSkipMissing(VoiceLoader, localizedId, overridePath))
        {
            ReportMissingAudio(
                AudioCategory.Voice,
                localizedId,
                VoiceLoader,
                overridePath);
            return AudioPlaybackResult.Skipped(AudioCategory.Voice, clipId, "voice");
        }

        var clip = await VoiceLoader.LoadAsync(
            localizedId, overridePath, cancellationToken);
        await mutations.WaitAsync(cancellationToken);
        try
        {
            await CompleteVoiceTransitionAsync(cancellationToken);
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
                AudioCategory.Voice, localizedId, "voice", clip, looping, volume,
                fade.DurationSeconds > 0 ? 0 : EffectiveVolume(AudioCategory.Voice, volume));
            lock (gate)
            {
                voice = playback;
                voiceActivityLease = activities?.BeginActivity(
                    RuntimeActivityOwner.Audio,
                    "Voice",
                    localizedId,
                    isCancelable: true,
                    isFinishable: true);
            }
            var transition = StartFade(
                [new AudioFadeTarget(playback, EffectiveVolume(playback))],
                fade,
                complete: () => ApplyVolume(playback),
                cancel: () => StopPlayback(playback));
            TrackVoiceTransition(transition);
            return new AudioPlaybackResult(playback, transition.Settlement);
        }
        finally
        {
            mutations.Release();
        }
    }

    public async Task StopVoiceAsync(
        AudioFade fade = default,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        fade = fade.Validate();
        AudioTransition? transition;
        await mutations.WaitAsync(cancellationToken);
        try
        {
            await CompleteVoiceTransitionAsync(cancellationToken);
            AudioPlayback? current;
            lock (gate) { current = voice; }
            if (current is null)
            {
                return;
            }

            transition = StartFade(
                [new AudioFadeTarget(current, 0)],
                fade,
                complete: () => StopPlayback(current),
                cancel: () => ApplyVolume(current));
            TrackVoiceTransition(transition);
        }
        finally
        {
            mutations.Release();
        }

        if (transition is not null)
        {
            await transition.Settlement.WaitAsync(cancellationToken);
        }
    }

    public async Task FinishTransitionsAsync(
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        AudioTransition[] snapshot;
        lock (gate)
        {
            snapshot = SelectTransitions(targetId);
        }

        foreach (var transition in snapshot)
        {
            await transition.FinishAsync(cancellationToken);
        }
    }

    public async Task CancelTransitionsAsync(
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        AudioTransition[] snapshot;
        lock (gate)
        {
            snapshot = SelectTransitions(targetId);
        }

        foreach (var transition in snapshot)
        {
            await transition.CancelAsync(cancellationToken);
        }
    }

    private AudioTransition[] SelectTransitions(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return bgmTransitions.Values
                .Concat(sfxTransitions.Values)
                .Append(voiceTransition)
                .Where(t => t is not null && !t.Settlement.IsCompleted)
                .Cast<AudioTransition>()
                .Distinct()
                .ToArray();
        }

        var trimmed = targetId.Trim();
        var list = new List<AudioTransition>();
        if (bgmTransitions.TryGetValue(trimmed, out var bgmTransition) && !bgmTransition.Settlement.IsCompleted)
        {
            list.Add(bgmTransition);
        }
        if (sfxTransitions.TryGetValue(trimmed, out var sfxTransition) && !sfxTransition.Settlement.IsCompleted)
        {
            list.Add(sfxTransition);
        }
        if (trimmed == "voice" && voiceTransition is not null && !voiceTransition.Settlement.IsCompleted)
        {
            list.Add(voiceTransition);
        }
        return list.ToArray();
    }

    private bool ShouldSkipMissing(
        IAudioClipLoader loader,
        string clipId,
        string? overridePath = null) =>
        !failOnMissingClips && !loader.Exists(clipId, overridePath);

    private void ReportMissingAudio(
        AudioCategory category,
        string clipId,
        IAudioClipLoader loader,
        string? overridePath = null)
    {
        var requestedPath = overridePath ?? (string.IsNullOrWhiteSpace(loader.PathPrefix)
            ? clipId
            : $"{loader.PathPrefix.TrimEnd('/')}/{clipId.TrimStart('/')}");
        missingResources?.Report(
            category.ToString(),
            clipId,
            "skip",
            requestedPath);
    }

    private AudioTransition StartFade(
        IReadOnlyList<AudioFadeTarget> targets,
        AudioFade fade,
        Action? complete,
        Action? cancel)
    {
        return new AudioTransition(targets, fade, activities, complete, cancel);
    }

    private AudioPlayback CreatePlayback(
        AudioCategory category,
        string clipId,
        string instanceId,
        IAudioClip clip,
        bool looping,
        float baseVolume,
        float initialVolume)
    {
        var backendPlayer = backend.CreatePlayer(category, clip, instanceId);
        backendPlayer.IsLooping = looping;
        backendPlayer.Volume = initialVolume;
        var playback = new AudioPlayback(
            category, clipId, instanceId, looping, baseVolume, backendPlayer);
        backendPlayer.Play();
        logger.Debug(
            "audio",
            $"audio.{category.ToString().ToLowerInvariant()}_started",
            $"{category} playback '{clipId}' started as '{instanceId}'.");
        PlaybackStarted?.Invoke(playback);
        _ = ObservePlaybackAsync(playback);
        return playback;
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

            if (playback.Category == AudioCategory.Bgm &&
                bgm.GetValueOrDefault(playback.InstanceId) == playback)
            {
                bgm.Remove(playback.InstanceId);
                bgmTransitions.TryGetValue(playback.InstanceId, out activeTransition);
            }
            else if (playback.Category == AudioCategory.Sfx &&
                sfx.GetValueOrDefault(playback.InstanceId) == playback)
            {
                sfx.Remove(playback.InstanceId);
                sfxTransitions.TryGetValue(playback.InstanceId, out activeTransition);
            }
            else if (playback.Category == AudioCategory.Voice && voice == playback)
            {
                voice = null;
                voiceActivityLease?.Dispose();
                voiceActivityLease = null;
                activeTransition = voiceTransition;
            }
        }

        activeTransition?.Finish();
        playback.Complete();
        logger.Debug(
            "audio",
            $"audio.{playback.Category.ToString().ToLowerInvariant()}_finished",
            $"{playback.Category} playback '{playback.ClipId}' reached its terminal state.");
        PlaybackStopped?.Invoke(playback);
    }

    private void StopPlayback(AudioPlayback playback)
    {
        lock (gate)
        {
            if (playback.State != AudioPlaybackState.Playing)
            {
                return;
            }

            playback.State = AudioPlaybackState.Stopped;
            if (playback.Category == AudioCategory.Bgm &&
                bgm.GetValueOrDefault(playback.InstanceId) == playback)
            {
                bgm.Remove(playback.InstanceId);
            }
            else if (playback.Category == AudioCategory.Sfx &&
                sfx.GetValueOrDefault(playback.InstanceId) == playback)
            {
                sfx.Remove(playback.InstanceId);
            }
            else if (playback.Category == AudioCategory.Voice && voice == playback)
            {
                voice = null;
                voiceActivityLease?.Dispose();
                voiceActivityLease = null;
            }
        }

        playback.Backend.StopPlayback();
        playback.Backend.Dispose();
        playback.Complete();
        logger.Debug(
            "audio",
            $"audio.{playback.Category.ToString().ToLowerInvariant()}_stopped",
            $"{playback.Category} playback '{playback.ClipId}' stopped.");
    }

    private void StopEveryPlayback()
    {
        AudioPlayback[] snapshot;
        lock (gate)
        {
            snapshot = bgm.Values.Concat(sfx.Values).Append(voice)
                .Where(value => value is not null)
                .Cast<AudioPlayback>()
                .Distinct()
                .ToArray();
            bgm.Clear();
            sfx.Clear();
            voice = null;
            voiceActivityLease?.Dispose();
            voiceActivityLease = null;
            bgmTransitions.Clear();
            sfxTransitions.Clear();
            voiceTransition = null;
        }

        foreach (var playback in snapshot)
        {
            StopPlayback(playback);
        }
    }

    private AudioPlayback[] SelectBgm(string? trackId)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(trackId))
            {
                return bgm.Values.ToArray();
            }

            return bgm.TryGetValue(trackId.Trim(), out var playback)
                ? [playback]
                : Array.Empty<AudioPlayback>();
        }
    }

    private AudioPlayback[] SelectSfx(string? instanceId)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return sfx.Values.ToArray();
            }

            return sfx.TryGetValue(instanceId.Trim(), out var playback)
                ? [playback]
                : Array.Empty<AudioPlayback>();
        }
    }

    private void TrackTransition(
        Dictionary<string, AudioTransition> transitions,
        string key,
        AudioTransition transition)
    {
        lock (gate)
        {
            transitions[key] = transition;
        }
        transition.Settled += () =>
        {
            lock (gate)
            {
                if (transitions.GetValueOrDefault(key) == transition)
                {
                    transitions.Remove(key);
                }
            }
        };
        _ = ClearTransitionAsync(transitions, key, transition);
    }

    private void TrackVoiceTransition(AudioTransition transition)
    {
        lock (gate)
        {
            voiceTransition = transition;
        }
        transition.Settled += () =>
        {
            lock (gate)
            {
                if (voiceTransition == transition)
                {
                    voiceTransition = null;
                }
            }
        };
        _ = ClearVoiceTransitionAsync(transition);
    }

    private AudioTransition?[] GetTransitions(
        Dictionary<string, AudioTransition> transitions,
        IEnumerable<AudioPlayback> entries)
    {
        lock (gate)
        {
            return entries
                .Select(entry => transitions.GetValueOrDefault(entry.InstanceId))
                .ToArray();
        }
    }

    private async Task ClearTransitionAsync(
        Dictionary<string, AudioTransition> transitions,
        string key,
        AudioTransition transition)
    {
        try { await transition.Settlement; } catch { }
        lock (gate)
        {
            if (transitions.GetValueOrDefault(key) == transition)
            {
                transitions.Remove(key);
            }
        }
    }

    private async Task ClearVoiceTransitionAsync(AudioTransition transition)
    {
        try { await transition.Settlement; } catch { }
        lock (gate)
        {
            if (voiceTransition == transition)
            {
                voiceTransition = null;
            }
        }
    }

    private Task CompleteVoiceTransitionAsync(CancellationToken cancellationToken)
    {
        AudioTransition? transition;
        lock (gate)
        {
            transition = voiceTransition;
        }

        return CompleteTransitionAsync(transition, cancellationToken);
    }

    private async Task CompleteTransitionsAsync(CancellationToken cancellationToken)
    {
        AudioTransition?[] snapshot;
        lock (gate)
        {
            snapshot = bgmTransitions.Values
                .Concat(sfxTransitions.Values)
                .Append(voiceTransition)
                .ToArray();
        }

        await CompleteTransitionsAsync(snapshot, cancellationToken);
    }

    private static async Task CompleteTransitionsAsync(
        IEnumerable<AudioTransition?> transitions,
        CancellationToken cancellationToken)
    {
        foreach (var transition in transitions.Where(value => value is not null)
                     .Cast<AudioTransition>().Distinct())
        {
            await CompleteTransitionAsync(transition, cancellationToken);
        }
    }

    private Task CompleteTransitionAsync(
        Dictionary<string, AudioTransition> transitions,
        string key,
        CancellationToken cancellationToken)
    {
        AudioTransition? transition;
        lock (gate)
        {
            transition = transitions.GetValueOrDefault(key);
        }

        return CompleteTransitionAsync(transition, cancellationToken);
    }

    private static async Task CompleteTransitionAsync(
        AudioTransition? transition,
        CancellationToken cancellationToken)
    {
        if (transition is null)
        {
            return;
        }

        await transition.FinishAsync(cancellationToken);
    }

    private void ApplyAllVolumes()
    {
        ApplyCategoryVolumes(AudioCategory.Bgm);
        ApplyCategoryVolumes(AudioCategory.Sfx);
        ApplyCategoryVolumes(AudioCategory.Voice);
    }

    private void ApplyCategoryVolumes(AudioCategory category)
    {
        AudioTransition[] transitions;
        lock (gate)
        {
            transitions = category switch
            {
                AudioCategory.Bgm => bgmTransitions.Values.Distinct().ToArray(),
                AudioCategory.Sfx => sfxTransitions.Values.Distinct().ToArray(),
                AudioCategory.Voice => voiceTransition is null ? [] : [voiceTransition],
                _ => [],
            };
        }

        foreach (var transition in transitions)
        {
            transition.Finish();
        }

        AudioPlayback[] snapshot;
        lock (gate)
        {
            snapshot = category switch
            {
                AudioCategory.Bgm => bgm.Values.ToArray(),
                AudioCategory.Sfx => sfx.Values.ToArray(),
                AudioCategory.Voice => voice is null ? [] : [voice],
                _ => [],
            };
        }

        foreach (var playback in snapshot)
        {
            ApplyVolume(playback);
        }

        if (transitions.Length > 0)
        {
            _ = ReapplyCategoryAfterTransitionsAsync(category, transitions);
        }
    }

    private async Task ReapplyCategoryAfterTransitionsAsync(
        AudioCategory category,
        IReadOnlyCollection<AudioTransition> transitions)
    {
        foreach (var transition in transitions)
        {
            try
            {
                await transition.Settlement;
            }
            catch
            {
            }
        }

        AudioPlayback[] snapshot;
        lock (gate)
        {
            snapshot = category switch
            {
                AudioCategory.Bgm => bgm.Values.ToArray(),
                AudioCategory.Sfx => sfx.Values.ToArray(),
                AudioCategory.Voice => voice is null ? [] : [voice],
                _ => [],
            };
        }

        foreach (var playback in snapshot)
        {
            ApplyVolume(playback);
        }
    }

    private void ApplyVolume(AudioPlayback playback) =>
        playback.Backend.Volume = EffectiveVolume(playback);

    private float EffectiveVolume(AudioPlayback playback) =>
        EffectiveVolume(playback.Category, playback.BaseVolume);

    private float EffectiveVolume(AudioCategory category, float baseVolume)
    {
        if (masterMuted || IsMuted(category))
        {
            return 0;
        }

        return masterVolume * CategoryVolume(category) * baseVolume;
    }

    private bool IsMuted(AudioCategory category) => category switch
    {
        AudioCategory.Bgm => bgmMuted,
        AudioCategory.Sfx => sfxMuted,
        AudioCategory.Voice => voiceMuted,
        _ => false,
    };

    private float CategoryVolume(AudioCategory category) => category switch
    {
        AudioCategory.Bgm => bgmVolume,
        AudioCategory.Sfx => sfxVolume,
        AudioCategory.Voice => voiceVolume,
        _ => 1,
    };

    private string ResolveLocalizedVoiceClipId(string clipId)
    {
        if (localizedResources is null || locales is null ||
            string.IsNullOrWhiteSpace(voiceLocale) || clipId.Contains('/'))
        {
            return !clipId.Contains('/') && !string.IsNullOrWhiteSpace(voiceLocale)
                ? $"{voiceLocale}/{clipId}"
                : clipId;
        }

        return localizedResources.ResolvePath(
            clipId,
            voiceLocale,
            locales.GetVoiceFallbackChain(voiceLocale),
            candidate => VoiceLoader.Exists(candidate)).ResolvedPath ?? $"{voiceLocale}/{clipId}";
    }

    private void HandleVoiceLocaleChanged(LocaleChangedEvent change) =>
        VoiceLocale = change.CurrentLocale;

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new AudioException("Audio manager is not initialized.");
        }
    }

    private static float ValidateVolume(float value)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), "Volume must be finite and between zero and one.");
        }

        return value;
    }

    private static string RequireName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public void Dispose()
    {
        voiceActivityLease?.Dispose();
        voiceActivityLease = null;
        lock (gate)
        {
            foreach (var transition in bgmTransitions.Values)
            {
                transition.Dispose();
            }
            bgmTransitions.Clear();

            foreach (var transition in sfxTransitions.Values)
            {
                transition.Dispose();
            }
            sfxTransitions.Clear();

            voiceTransition?.Dispose();
            voiceTransition = null;
        }
        mutations.Dispose();
    }
}
