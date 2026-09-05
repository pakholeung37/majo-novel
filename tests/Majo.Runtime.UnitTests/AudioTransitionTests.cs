#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class AudioTransitionTests
{
    [Fact(Timeout = 5_000)]
    public async Task AudioCrossfade_NaturalCompletion_And_Preemption()
    {
        var harness = await TestAudioHarness.CreateAsync(autoCompleteFades: false);
        var track1 = await harness.Audio.PlayBgmAsync("calm", "music");

        // Natural completion
        harness.Backend.CompleteFades();
        await track1.Settlement;
        Assert.Equal(AudioPlaybackState.Playing, track1.Playback.State);

        // Preempt with second track
        var track2 = await harness.Audio.PlayBgmAsync("battle", "music", fade: new AudioFade(1));
        Assert.Equal(2, harness.Backend.ActivePlayers.Count);

        // Finish second fade
        harness.Backend.CompleteFades();
        await track2.Settlement;

        Assert.Single(harness.Backend.ActivePlayers);
        Assert.Equal(AudioPlaybackState.Stopped, track1.Playback.State);
        Assert.Equal(AudioPlaybackState.Playing, track2.Playback.State);

        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task Audio_ResetAndShutdown_CancelsTransitionsAndClearsPlayers()
    {
        var harness = await TestAudioHarness.CreateAsync(autoCompleteFades: false);
        var bgm = await harness.Audio.PlayBgmAsync("calm", "music", fade: new AudioFade(5));
        var voice = await harness.Audio.PlayVoiceAsync("line-1");

        Assert.NotNull(harness.Audio.CurrentVoice);

        // Reset stops voice and clears players
        await harness.Host.ResetAsync();

        Assert.Null(harness.Audio.CurrentVoice);
        Assert.Empty(harness.Audio.BgmTracks);
        Assert.Empty(harness.Backend.ActivePlayers);

        await harness.Host.ShutdownAsync();
    }

    private sealed class StubFrameClock : IFrameClock
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NextFrameAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestAudioHarness
    {
        public RuntimeHost Host { get; }
        public FakeAudioBackend Backend { get; }
        public IAudioManager Audio { get; }

        private TestAudioHarness(RuntimeHost host, FakeAudioBackend backend, IAudioManager audio)
        {
            Host = host;
            Backend = backend;
            Audio = audio;
        }

        public static async Task<TestAudioHarness> CreateAsync(bool autoCompleteFades = true)
        {
            var backend = new FakeAudioBackend { AutoCompleteFades = autoCompleteFades };
            var bgm = new FakeAudioClipLoader("audio/bgm").Register("calm").Register("battle");
            var sfx = new FakeAudioClipLoader("audio/sfx").Register("hit");
            var voice = new FakeAudioClipLoader("audio/voice").Register("en/line-1").Register("en/line-2");
            var frameClock = new StubFrameClock();
            var missingResources = new MissingResourceReporter();

            var registry = new RuntimeServiceRegistry()
                .AddSingleton<IFrameClock>(_ => frameClock)
                .AddSingleton<IMissingResourceReporter>(_ => missingResources)
                .AddSingleton<IOperationRunner>(
                    resolver => new OperationRunner(
                        resolver.Get<IFrameClock>(),
                        resolver.Get<IRuntimeLifetime>()),
                    typeof(IFrameClock),
                    typeof(IRuntimeLifetime))
                .AddSingleton<IAudioManager>(
                    resolver => new AudioManager(
                        backend,
                        bgm,
                        sfx,
                        voice,
                        new AudioSettings(),
                        missingResources: resolver.Get<IMissingResourceReporter>()),
                    typeof(IMissingResourceReporter));

            var host = registry.Build();
            await host.InitializeAsync();
            return new TestAudioHarness(host, backend, host.Services.Get<IAudioManager>());
        }
    }
}
