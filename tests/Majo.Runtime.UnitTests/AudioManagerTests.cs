using Majo.Runtime.Audio;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Commands.Audio;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class AudioManagerTests
{
    [Fact(Timeout = 5_000)]
    public async Task BgmCrossfadeKeepsBothPlayersUntilFadeCompletes()
    {
        var harness = await AudioHarness.CreateAsync(autoCompleteFades: false);
        await harness.Audio.PlayBgmAsync("calm", "music");

        var result = await harness.Audio.PlayBgmAsync(
            "battle", "music", fade: new AudioFade(1, "crossfade", "audio"));

        Assert.Equal(2, harness.Backend.ActivePlayers.Count);
        Assert.Equal("battle", Assert.Single(harness.Audio.BgmTracks).ClipId);
        Assert.Equal(0, result.Playback.Volume);
        Assert.False(result.FadeOperation.Completion.IsCompleted);

        harness.Backend.CompleteFades();
        await result.FadeOperation.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Single(harness.Backend.ActivePlayers);
        Assert.Equal(1, result.Playback.Volume);
        Assert.Equal(OperationState.Completed, result.FadeOperation.State);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CrossfadeCancelRestoresOldTrackWhileCompleteAppliesNewTrack()
    {
        var harness = await AudioHarness.CreateAsync(autoCompleteFades: false);
        var original = (await harness.Audio.PlayBgmAsync("calm", "music")).Playback;
        var cancelled = await harness.Audio.PlayBgmAsync(
            "battle", "music", fade: new AudioFade(1, "cancel-me"));

        cancelled.FadeOperation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled.FadeOperation.Completion);

        Assert.Equal(AudioPlaybackState.Stopped, cancelled.Playback.State);
        Assert.Same(original, Assert.Single(harness.Audio.BgmTracks));
        Assert.Equal(AudioPlaybackState.Playing, original.State);

        var completed = await harness.Audio.PlayBgmAsync(
            "battle", "music", fade: new AudioFade(1, "complete-me"));
        completed.FadeOperation.Complete();
        await completed.FadeOperation.Completion;

        Assert.Equal(AudioPlaybackState.Stopped, original.State);
        Assert.Same(completed.Playback, Assert.Single(harness.Audio.BgmTracks));
        Assert.Equal(1, completed.Playback.Volume);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task SfxOverlapsAndNamedInstancesCanBeStoppedIndependently()
    {
        var harness = await AudioHarness.CreateAsync();
        var first = await harness.Audio.PlaySfxAsync("hit", "left");
        var second = await harness.Audio.PlaySfxAsync("hit", "right");

        Assert.Equal(2, harness.Audio.SfxInstances.Count);
        await Assert.ThrowsAsync<DuplicateAudioInstanceException>(() =>
            harness.Audio.PlaySfxAsync("hit", "left"));

        var stop = await harness.Audio.StopSfxAsync("left");
        await stop.Completion;

        Assert.Equal(AudioPlaybackState.Stopped, first.Playback.State);
        Assert.Equal(AudioPlaybackState.Playing, second.Playback.State);
        Assert.Equal("right", Assert.Single(harness.Audio.SfxInstances).InstanceId);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task NaturalSfxCompletionAlsoSettlesAnOutstandingFade()
    {
        var harness = await AudioHarness.CreateAsync(autoCompleteFades: false);
        var result = await harness.Audio.PlaySfxAsync(
            "hit", "short", fade: new AudioFade(10, "long-fade"));
        var backendPlayer = harness.Backend.Players.Single(
            player => player.InstanceId == "short");

        backendPlayer.CompletePlayback();
        await result.FadeOperation.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        await result.Playback.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(AudioPlaybackState.Finished, result.Playback.State);
        Assert.Empty(harness.Audio.SfxInstances);
        Assert.Equal(OperationState.Completed, result.FadeOperation.State);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task NewVoicePreemptsPreviousAndMixerControlsApplyImmediately()
    {
        var harness = await AudioHarness.CreateAsync();
        harness.Audio.MasterVolume = 0.5f;
        harness.Audio.VoiceVolume = 0.4f;
        var first = await harness.Audio.PlayVoiceAsync("line-1", volume: 0.5f);

        Assert.Equal(0.1f, first.Playback.Volume, 3);
        var second = await harness.Audio.PlayVoiceAsync("line-2");

        Assert.Equal(AudioPlaybackState.Stopped, first.Playback.State);
        Assert.Same(second.Playback, harness.Audio.CurrentVoice);
        harness.Audio.VoiceMuted = true;
        Assert.Equal(0, second.Playback.Volume);
        harness.Audio.VoiceMuted = false;
        Assert.Equal(0.2f, second.Playback.Volume, 3);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task MissingClipsAreSkippedWithoutMutatingPlaybackAndAreCounted()
    {
        var harness = await AudioHarness.CreateAsync();
        var original = (await harness.Audio.PlayBgmAsync("calm", "music")).Playback;

        var first = await harness.Audio.PlayBgmAsync("missing", "music");
        var second = await harness.Audio.PlayBgmAsync("missing", "music");

        Assert.True(first.IsSkipped);
        Assert.True(second.IsSkipped);
        Assert.Equal(AudioPlaybackState.Finished, first.Playback.State);
        Assert.Same(original, Assert.Single(harness.Audio.BgmTracks));
        Assert.Single(harness.Backend.ActivePlayers);
        var occurrence = Assert.Single(harness.MissingResources.Occurrences);
        Assert.Equal("Bgm", occurrence.ResourceType);
        Assert.Equal("missing", occurrence.ResourceId);
        Assert.Equal("skip", occurrence.Fallback);
        Assert.Equal(2, occurrence.Count);

        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task StrictMissingClipModeStillThrows()
    {
        var harness = await AudioHarness.CreateAsync(failOnMissingClips: true);

        await Assert.ThrowsAsync<AudioException>(() =>
            harness.Audio.PlaySfxAsync("missing"));
        Assert.Empty(harness.MissingResources.Occurrences);

        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task MixerChangeDuringFadeCannotBeOverwrittenByTheOldTweenTarget()
    {
        var harness = await AudioHarness.CreateAsync(autoCompleteFades: false);
        var result = await harness.Audio.PlayBgmAsync(
            "calm", "music", fade: new AudioFade(10, "volume-fade"));

        harness.Audio.MasterVolume = 0.5f;
        await result.FadeOperation.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0.5f, result.Playback.Volume, 3);
        var modified = await harness.Audio.PlayBgmAsync(
            "calm", "music", loop: false, fade: new AudioFade());
        await modified.FadeOperation.Completion;
        Assert.False(modified.Playback.Loop);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task RuntimeResetClearsPlayersAfterSchedulerCancelsFades()
    {
        var harness = await AudioHarness.CreateAsync(autoCompleteFades: false);
        var bgm = await harness.Audio.PlayBgmAsync(
            "calm", "music", fade: new AudioFade(1, "bgm-fade"));
        var sfx = await harness.Audio.PlaySfxAsync(
            "hit", "impact", fade: new AudioFade(1, "sfx-fade"));

        await harness.Host.ResetAsync();

        Assert.Equal(OperationState.Cancelled, bgm.FadeOperation.State);
        Assert.Equal(OperationState.Cancelled, sfx.FadeOperation.State);
        Assert.Empty(harness.Operations.ActiveOperations);
        Assert.Empty(harness.Backend.ActivePlayers);
        Assert.Empty(harness.Audio.BgmTracks);
        Assert.Empty(harness.Audio.SfxInstances);

        await harness.Host.ShutdownAsync();
        Assert.Empty(harness.Backend.ActivePlayers);
    }

    [Fact(Timeout = 5_000)]
    public async Task VoicePresenterUsesConventionAndSkipsMissingMetadataOverride()
    {
        var harness = await AudioHarness.CreateAsync();
        var presenter = new VoiceDialoguePresenter(harness.Audio);
        var context = PresentationContext();
        var conventional = Line("line-1");

        await presenter.PresentLineAsync(conventional, context);
        Assert.Equal("line-1", presenter.LastVoiceId);
        Assert.Equal("en/line-1", harness.Audio.CurrentVoice?.ClipId);

        await presenter.PresentLineAsync(Line("line-x", "voice:line-2"), context);
        Assert.Equal("line-2", presenter.LastVoiceId);
        Assert.Equal("en/line-2", harness.Audio.CurrentVoice?.ClipId);

        await presenter.PresentLineAsync(Line("line-x", "voice:missing"), context);
        Assert.Null(presenter.LastVoiceId);
        Assert.Null(harness.Audio.CurrentVoice);
        Assert.Empty(harness.MissingResources.Occurrences);
        await harness.Host.ShutdownAsync();
    }

    [Fact]
    public void AudioCommandModuleRegistersTypedCommandsAndAudioFadeDefault()
    {
        var registry = new RuntimeCommandRegistry();
        var backend = new FakeAudioBackend();
        var loader = new FakeAudioClipLoader("audio").Register("unused");
        var operations = new StubOperationScheduler();
        var audio = new AudioManager(
            backend, operations, loader, loader, loader, new AudioSettings());
        registry.RegisterModule(new AudioCommandModule(audio));
        var catalog = registry.BuildCatalog();
        var descriptor = catalog.Get("bgm").Descriptor;
        var binder = new CommandBinder(new CommandBindingContext(0.5, 1.25));

        var arguments = binder.Bind<PlayBgmCommandArguments>(descriptor, ["theme"]);

        Assert.Equal(1.25, arguments.Duration);
        Assert.True(arguments.Loop);
        Assert.Equal(
            ["bgm", "stopBgm", "sfx", "stopSfx", "voice", "stopVoice"],
            catalog.Commands.Select(command => command.Name));
    }

    private static DialogueLine Line(string id, params string[] metadata) => new(
        id,
        "Maya",
        new DialogueText("Text", Array.Empty<DialogueMarkupAttribute>()),
        metadata);

    private static DialoguePresentationContext PresentationContext() => new(
        CancellationToken.None,
        CancellationToken.None,
        () => { },
        () => { });

    private sealed class AudioHarness
    {
        private AudioHarness(
            RuntimeHost host,
            FakeAudioBackend backend,
            IAudioManager audio,
            IOperationScheduler operations,
            IMissingResourceReporter missingResources)
        {
            Host = host;
            Backend = backend;
            Audio = audio;
            Operations = operations;
            MissingResources = missingResources;
        }

        public RuntimeHost Host { get; }

        public FakeAudioBackend Backend { get; }

        public IAudioManager Audio { get; }

        public IOperationScheduler Operations { get; }

        public IMissingResourceReporter MissingResources { get; }

        public static async Task<AudioHarness> CreateAsync(
            bool autoCompleteFades = true,
            bool failOnMissingClips = false)
        {
            var backend = new FakeAudioBackend { AutoCompleteFades = autoCompleteFades };
            var bgm = new FakeAudioClipLoader("audio/bgm")
                .Register("calm")
                .Register("battle");
            var sfx = new FakeAudioClipLoader("audio/sfx").Register("hit");
            var voice = new FakeAudioClipLoader("audio/voice")
                .Register("en/line-1")
                .Register("en/line-2");
            var frameClock = new ImmediateFrameClock();
            var missingResources = new MissingResourceReporter();
            var registry = new RuntimeServiceRegistry()
                .AddSingleton<IFrameClock>(_ => frameClock)
                .AddSingleton<IMissingResourceReporter>(_ => missingResources)
                .AddSingleton<IOperationScheduler>(
                    resolver => new OperationScheduler(
                        resolver.Get<IFrameClock>(),
                        resolver.Get<IRuntimeLifetime>()),
                    typeof(IFrameClock),
                    typeof(IRuntimeLifetime))
                .AddSingleton<IAudioManager>(
                    resolver => new AudioManager(
                        backend,
                        resolver.Get<IOperationScheduler>(),
                        bgm,
                        sfx,
                        voice,
                        new AudioSettings(FailOnMissingClips: failOnMissingClips),
                        missingResources: resolver.Get<IMissingResourceReporter>()),
                    typeof(IOperationScheduler),
                    typeof(IMissingResourceReporter));
            var host = registry.Build();
            await host.InitializeAsync();
            return new AudioHarness(
                host,
                backend,
                host.Services.Get<IAudioManager>(),
                host.Services.Get<IOperationScheduler>(),
                host.Services.Get<IMissingResourceReporter>());
        }
    }

    private sealed class ImmediateFrameClock : IFrameClock
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NextFrameAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubOperationScheduler : IOperationScheduler
    {
        public IReadOnlyCollection<IOperationHandle> ActiveOperations => [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IOperationHandle Start(OperationDefinition definition) =>
            throw new NotSupportedException();

        public Task WaitAsync(OperationTarget target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(OperationTarget target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompleteAsync(OperationTarget target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
