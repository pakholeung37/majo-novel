#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;
using Majo.Runtime.State;

namespace Majo.Runtime.Audio;

public sealed class AudioManager : IAudioManager, IAudioReplacementSource, IStateMutationParticipant, IDisposable
{
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly IAudioBackend backend;
    private readonly IRuntimeLogger logger;
    private readonly AudioMixer mixer;
    private readonly AudioResourceGuard resourceGuard;
    private readonly AudioTransitionCoordinator transitions;
    private readonly BgmChannel bgm;
    private readonly SfxChannel sfx;
    private readonly VoiceChannel voice;

    private long bgmEpoch;
    private bool bgmSceneClosing;
    private bool isInitialized;
    private bool backendInitializationAttempted;
    private StateMutationGate? mutationGate;

    public void BindStateMutationGate(StateMutationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (mutationGate is not null && !ReferenceEquals(mutationGate, gate))
            throw new StateCompatibilityException("Audio manager is already bound to another runtime's state mutation gate.");
        mutationGate = gate;
    }

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

        ArgumentNullException.ThrowIfNull(settings);
        mixer = new AudioMixer(settings);
        resourceGuard = new AudioResourceGuard(settings, missingResources, locales, localizedResources);
        transitions = new AudioTransitionCoordinator(activities);

        bgm = new BgmChannel(
            this.backend, mixer, transitions, this.logger,
            p => PlaybackStarted?.Invoke(p),
            p => PlaybackStopped?.Invoke(p));
        sfx = new SfxChannel(
            this.backend, mixer, transitions, this.logger,
            p => PlaybackStarted?.Invoke(p),
            p => PlaybackStopped?.Invoke(p));
        voice = new VoiceChannel(
            this.backend, mixer, transitions, activities, this.logger,
            p => PlaybackStarted?.Invoke(p),
            p => PlaybackStopped?.Invoke(p));

        mixer.VolumeChanged += HandleVolumeChanged;
    }

    public event Action<IAudioPlayback>? PlaybackStarted;

    public event Action<IAudioPlayback>? PlaybackStopped;

    public float MasterVolume
    {
        get => mixer.MasterVolume;
        set => mixer.MasterVolume = value;
    }

    public float BgmVolume
    {
        get => mixer.BgmVolume;
        set => mixer.BgmVolume = value;
    }

    public float SfxVolume
    {
        get => mixer.SfxVolume;
        set => mixer.SfxVolume = value;
    }

    public float VoiceVolume
    {
        get => mixer.VoiceVolume;
        set => mixer.VoiceVolume = value;
    }

    public bool MasterMuted
    {
        get => mixer.MasterMuted;
        set => mixer.MasterMuted = value;
    }

    public bool BgmMuted
    {
        get => mixer.BgmMuted;
        set => mixer.BgmMuted = value;
    }

    public bool SfxMuted
    {
        get => mixer.SfxMuted;
        set => mixer.SfxMuted = value;
    }

    public bool VoiceMuted
    {
        get => mixer.VoiceMuted;
        set => mixer.VoiceMuted = value;
    }

    public string VoiceLocale
    {
        get => resourceGuard.VoiceLocale;
        set => resourceGuard.VoiceLocale = value;
    }

    public IAudioClipLoader BgmLoader { get; }

    public IAudioClipLoader SfxLoader { get; }

    public IAudioClipLoader VoiceLoader { get; }

    public IReadOnlyCollection<IAudioPlayback> BgmTracks => bgm.Tracks;

    public IReadOnlyList<BgmRestoreTarget> CaptureCommittedBgm() => bgm.CaptureCommittedTargets();

    public IReadOnlyCollection<IAudioPlayback> SfxInstances => sfx.Instances;

    public IAudioPlayback? CurrentVoice => voice.CurrentVoice;

    public bool IsFading(string instanceId) => transitions.IsFading(instanceId);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (isInitialized)
        {
            return;
        }

        backendInitializationAttempted = true;
        await backend.InitializeAsync(cancellationToken);
        resourceGuard.Initialize();
        OpenSceneRequests();
        isInitialized = true;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        CloseSceneRequests();

        EnsureInitialized();
        await mutations.WaitAsync(cancellationToken);
        try
        {
            await transitions.CompleteAllTransitionsAsync(cancellationToken);
            bgm.StopAll();
            sfx.Reset();
            voice.StopAll();
            transitions.ClearAll();
            await backend.ResetAsync(cancellationToken);
        }
        finally
        {
            mutations.Release();
            OpenSceneRequests();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        CloseSceneRequests();

        if (!isInitialized && !backendInitializationAttempted)
        {
            return;
        }

        await mutations.WaitAsync(cancellationToken);
        try
        {
            await transitions.CompleteAllTransitionsAsync(cancellationToken);
            bgm.StopAll();
            sfx.StopAll();
            voice.StopAll();
            transitions.ClearAll();
            await backend.ShutdownAsync(cancellationToken);
            resourceGuard.Shutdown();
            isInitialized = false;
            backendInitializationAttempted = false;
        }
        finally
        {
            mutations.Release();
        }
    }

    public void CloseSceneRequests()
    {
        Volatile.Write(ref bgmSceneClosing, true);
        Interlocked.Increment(ref bgmEpoch);
    }
    public void OpenSceneRequests() => Volatile.Write(ref bgmSceneClosing, false);

    public async Task StopExecutionForRestoreAsync(CancellationToken cancellationToken)
    {
        CloseSceneRequests();
        await mutations.WaitAsync(cancellationToken);
        try { await transitions.CancelTransitionsAsync(null, cancellationToken); }
        finally { mutations.Release(); OpenSceneRequests(); }
    }

    public async Task<IPreparedAudioReplacement> PrepareReplacementAsync(
        IReadOnlyList<BgmRestoreTarget> tracks, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (tracks.Select(track => track.TrackId).Distinct(StringComparer.Ordinal).Count() != tracks.Count)
            throw new AudioException("BGM replacement contains duplicate track IDs.");
        await mutations.WaitAsync(cancellationToken);
        var candidates = new List<AudioPlayback>();
        try
        {
            foreach (var track in tracks)
            {
                AudioResourceGuard.RequireName(track.ClipId, nameof(track.ClipId));
                AudioResourceGuard.RequireName(track.TrackId, nameof(track.TrackId));
                AudioMixer.ValidateVolume(track.Volume);
                var clip = await BgmLoader.LoadAsync(track.ClipId, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                candidates.Add(bgm.PreparePlayer(track, clip));
            }
            return new PreparedReplacement(this, candidates.ToArray());
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var candidate in candidates)
                try { candidate.Backend.Dispose(); }
                catch (Exception cleanup) { failures.Add(cleanup); }
            mutations.Release();
            if (failures.Count > 1) throw new AggregateException("Audio preparation and cleanup failed.", failures);
            throw;
        }
    }

    private sealed class PreparedReplacement(AudioManager owner, AudioPlayback[] candidates) : IPreparedAudioReplacement
    {
        private bool published;
        private bool released;
        private bool disposed;
        public void Commit()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (released) throw new AudioException("Audio replacement was already committed.");
            try
            {
                owner.Mutate(() =>
                {
                    owner.sfx.StopAll();
                    owner.voice.StopAll();
                    owner.transitions.ClearAll();
                    // Published objects belong to the channel even if a plugin Play callback fails.
                    published = true;
                    owner.bgm.ReplacePrepared(candidates);
                });
            }
            finally { ReleaseGate(); }
        }
        private void ReleaseGate()
        {
            if (released) return;
            released = true;
            owner.mutations.Release();
        }
        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            try
            {
                if (!published)
                {
                    List<Exception>? failures = null;
                    foreach (var candidate in candidates)
                        try { candidate.Backend.Dispose(); }
                        catch (Exception cleanup) { (failures ??= new()).Add(cleanup); }
                    if (failures is not null) throw new AggregateException("Audio candidate cleanup failed.", failures);
                }
            }
            finally { ReleaseGate(); }
            return ValueTask.CompletedTask;
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
        var requestEpoch = Volatile.Read(ref bgmEpoch);
        if (Volatile.Read(ref bgmSceneClosing))
            throw new InvalidOperationException("The BGM scene is closing.");
        EnsureInitialized();
        clipId = AudioResourceGuard.RequireName(clipId, nameof(clipId));
        trackId = string.IsNullOrWhiteSpace(trackId) ? clipId : trackId.Trim();
        volume = AudioMixer.ValidateVolume(volume);
        fade = fade.Validate();
        if (resourceGuard.ShouldSkipMissing(BgmLoader, clipId))
        {
            resourceGuard.ReportMissingAudio(AudioCategory.Bgm, clipId, BgmLoader);
            return AudioPlaybackResult.Skipped(AudioCategory.Bgm, clipId, trackId);
        }

        var clip = await BgmLoader.LoadAsync(clipId, cancellationToken: cancellationToken);

        return await MutateAsync(async token =>
        {
            await mutations.WaitAsync(token);
            try
            {
                if (requestEpoch != Volatile.Read(ref bgmEpoch) || Volatile.Read(ref bgmSceneClosing))
                    return AudioPlaybackResult.Skipped(AudioCategory.Bgm, clipId, trackId);
                return await bgm.PlayAsync(clipId, trackId, clip, looping, volume, fade, token);
            }
            finally { mutations.Release(); }
        }, cancellationToken);
    }

    public async Task StopBgmAsync(
        string? trackId = null,
        AudioFade fade = default,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        fade = fade.Validate();
        Task? settlementTask;
        settlementTask = await MutateAsync(async token =>
        {
            await mutations.WaitAsync(token);
            try { return await bgm.StopAsync(trackId, fade, token); }
            finally { mutations.Release(); }
        }, cancellationToken);

        if (settlementTask is not null)
        {
            await settlementTask.WaitAsync(cancellationToken);
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
        clipId = AudioResourceGuard.RequireName(clipId, nameof(clipId));
        volume = AudioMixer.ValidateVolume(volume);
        fade = fade.Validate();
        instanceId = sfx.ResolveInstanceId(instanceId);
        if (resourceGuard.ShouldSkipMissing(SfxLoader, clipId))
        {
            resourceGuard.ReportMissingAudio(AudioCategory.Sfx, clipId, SfxLoader);
            return AudioPlaybackResult.Skipped(AudioCategory.Sfx, clipId, instanceId);
        }

        var clip = await SfxLoader.LoadAsync(clipId, cancellationToken: cancellationToken);
        await mutations.WaitAsync(cancellationToken);
        try
        {
            return await sfx.PlayAsync(
                clipId, instanceId, clip, looping, volume, fade, cancellationToken);
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
        Task? settlementTask;
        await mutations.WaitAsync(cancellationToken);
        try
        {
            settlementTask = await sfx.StopAsync(instanceId, fade, cancellationToken);
        }
        finally
        {
            mutations.Release();
        }

        if (settlementTask is not null)
        {
            await settlementTask.WaitAsync(cancellationToken);
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
        clipId = AudioResourceGuard.RequireName(clipId, nameof(clipId));
        volume = AudioMixer.ValidateVolume(volume);
        fade = fade.Validate();
        var localizedId = resourceGuard.ResolveLocalizedVoiceClipId(clipId, VoiceLoader);
        if (resourceGuard.ShouldSkipMissing(VoiceLoader, localizedId, overridePath))
        {
            resourceGuard.ReportMissingAudio(
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
            return await voice.PlayAsync(
                clipId, localizedId, clip, looping, volume, fade, cancellationToken);
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
        Task? settlementTask;
        await mutations.WaitAsync(cancellationToken);
        try
        {
            settlementTask = await voice.StopAsync(fade, cancellationToken);
        }
        finally
        {
            mutations.Release();
        }

        if (settlementTask is not null)
        {
            await settlementTask.WaitAsync(cancellationToken);
        }
    }

    public Task FinishTransitionsAsync(
        string? targetId = null,
        CancellationToken cancellationToken = default) =>
        transitions.FinishTransitionsAsync(targetId, cancellationToken);

    public Task CancelTransitionsAsync(
        string? targetId = null,
        CancellationToken cancellationToken = default) =>
        transitions.CancelTransitionsAsync(targetId, cancellationToken);

    public void Dispose()
    {
        voice.Dispose();
        transitions.Dispose();
        mutations.Dispose();
    }

    private void HandleVolumeChanged(AudioCategory? category)
    {
        if (category is null)
        {
            bgm.ApplyVolumes();
            sfx.ApplyVolumes();
            voice.ApplyVolumes();
        }
        else
        {
            switch (category.Value)
            {
                case AudioCategory.Bgm:
                    bgm.ApplyVolumes();
                    break;
                case AudioCategory.Sfx:
                    sfx.ApplyVolumes();
                    break;
                case AudioCategory.Voice:
                    voice.ApplyVolumes();
                    break;
            }
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new AudioException("Audio manager is not initialized.");
        }
    }

    private void Mutate(Action mutation)
    {
        if (mutationGate is null) mutation();
        else mutationGate.Write(mutation);
    }

    private Task<T> MutateAsync<T>(
        Func<CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken) => mutationGate is null
        ? mutation(cancellationToken)
        : mutationGate.WriteAsync(mutation, cancellationToken);
}
