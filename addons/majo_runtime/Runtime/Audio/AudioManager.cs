#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Audio;

public sealed class AudioManager : IAudioManager, IDisposable
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

    private bool isInitialized;
    private bool backendInitializationAttempted;

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
        isInitialized = true;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
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

    public async Task<AudioPlaybackResult> PlayBgmAsync(
        string clipId,
        string? trackId = null,
        bool looping = true,
        float volume = 1,
        AudioFade fade = default,
        CancellationToken cancellationToken = default)
    {
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

        await mutations.WaitAsync(cancellationToken);
        try
        {
            return await bgm.PlayAsync(
                clipId, trackId, clip, looping, volume, fade, cancellationToken);
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
        Task? settlementTask;
        await mutations.WaitAsync(cancellationToken);
        try
        {
            settlementTask = await bgm.StopAsync(trackId, fade, cancellationToken);
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
}
