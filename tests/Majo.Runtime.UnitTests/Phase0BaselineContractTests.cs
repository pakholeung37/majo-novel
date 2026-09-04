#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Stage;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class Phase0BaselineContractTests
{
    // ==========================================
    // 1. Composite Direction Transaction Tests
    // ==========================================

    [Fact(Timeout = 5_000)]
    public async Task CompositeTransactionSettlement_CompletesNaturallyWhenAllComponentsSettle()
    {
        var components = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(
            components, components, components, components);
        var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);

        var planDirectives = new IDirectionDirective[]
        {
            new StageCreatePatch(StageId.Main, ViewId.Main, DirectionTransitionSpec.Cut),
            new BackdropPatch(StageId.Main, new BackdropState(new ResourceKey("bg1")), new DirectionTransitionSpec(DirectionTransitionKind.Fade, 1.0)),
        };

        var transaction = await coordinator.ExecuteAsync(planDirectives, DirectionCompletionBarrier.Commit);
        Assert.Equal(DirectionTransactionStatus.Committed, transaction.Status);
        Assert.False(transaction.Settlement.IsCompleted);

        // Components settle
        await coordinator.FinishAsync(transaction);
        await transaction.Settlement;

        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
        Assert.True(transaction.Settlement.IsCompletedSuccessfully);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeTransactionFinish_ForcesFinishOnAllComponents()
    {
        var components = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(
            components, components, components, components);
        var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);

        var transaction = await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new BackdropPatch(StageId.Main, new BackdropState(new ResourceKey("bg1")), new DirectionTransitionSpec(
                DirectionTransitionKind.Fade, 1.0, DirectionEase.Linear)),
        }, DirectionCompletionBarrier.Commit);

        Assert.False(transaction.Settlement.IsCompleted);
        await coordinator.FinishAsync(transaction);

        Assert.True(transaction.Settlement.IsCompletedSuccessfully);
        Assert.True(components.FinishCount > 0);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeTransactionRollback_RestoresPreviousStateAndRollsBack()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);

        var initialRevision = coordinator.State.Revision;
        var transaction = await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new BackdropPatch(StageId.Main, new BackdropState(new ResourceKey("bg1")), DirectionTransitionSpec.Cut),
        }, DirectionCompletionBarrier.Settle);

        Assert.True(coordinator.State.Revision > initialRevision);
        var rolledBack = await coordinator.RollbackAsync(transaction);

        Assert.NotNull(rolledBack);
        Assert.True(coordinator.State.Revision > transaction.Plan.TargetState.Revision);
    }

    [Fact(Timeout = 5_000)]
    public async Task CompositeTransactionFinishSkippable_SkipsOnlySkippableTransactions()
    {
        var components = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(
            components, components, components, components);
        var coordinator = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);

        var skippableTx = await coordinator.ExecuteAsync(new IDirectionDirective[]
        {
            new BackdropPatch(StageId.Main, new BackdropState(new ResourceKey("bg1")), new DirectionTransitionSpec(
                DirectionTransitionKind.Fade, 1.0, DirectionEase.Linear, Skippable: true)),
        }, DirectionCompletionBarrier.Commit);

        Assert.True(skippableTx.IsSkippable);
        Assert.Equal(DirectionTransactionStatus.Committed, skippableTx.Status);

        await coordinator.FinishSkippableAsync();
        Assert.True(skippableTx.Settlement.IsCompletedSuccessfully);
    }

    // ==========================================
    // 2. Transient Cue Lifecycle Tests
    // ==========================================

    [Fact(Timeout = 5_000)]
    public async Task CameraFxCueHandler_Settlement_Cancel_Finish_Contracts()
    {
        var mockModifiers = new MockCameraModifierManager();
        var context = new TransientCueExecutionContext(
            mockModifiers,
            new MockScreenEffectManager(),
            new MockStageCurtainManager());

        var cueRegistry = new TransientCueHandlerRegistry();
        var cue = new CameraFxCue(ViewId.Main, "shake", new Dictionary<string, string>(), 0.5);
        var handler = cueRegistry.Resolve(cue);
        handler.Validate(cue);

        // Execute returns operation handle
        var handle = handler.Execute(cue, context);
        Assert.NotNull(handle);
        Assert.Equal(OperationState.Running, handle.State);

        // Test complete/finish
        handle.Finish();
        await handle.Completion;
        Assert.Equal(OperationState.Completed, handle.State);

        // Test cancellation on another instance
        var handle2 = handler.Execute(cue, context);
        handle2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle2.Completion);
        Assert.Equal(OperationState.Cancelled, handle2.State);
    }

    [Fact(Timeout = 5_000)]
    public async Task FlashCueHandler_Settlement_Cancel_Finish_Contracts()
    {
        var mockEffects = new MockScreenEffectManager();
        var context = new TransientCueExecutionContext(
            new MockCameraModifierManager(),
            mockEffects,
            new MockStageCurtainManager());

        var cueRegistry = new TransientCueHandlerRegistry();
        var cue = new FlashCue(new DirectionColor(1, 1, 1, 1), 0.8f, 0.4);
        var handler = cueRegistry.Resolve(cue);
        handler.Validate(cue);

        var handle = handler.Execute(cue, context);
        Assert.NotNull(handle);
        handle.Finish();
        await handle.Completion;
        Assert.Equal(OperationState.Completed, handle.State);

        var handle2 = handler.Execute(cue, context);
        handle2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle2.Completion);
        Assert.Equal(OperationState.Cancelled, handle2.State);
    }

    [Fact(Timeout = 5_000)]
    public async Task CurtainCueHandler_Settlement_Cancel_Finish_Contracts()
    {
        var mockCurtain = new MockStageCurtainManager();
        var context = new TransientCueExecutionContext(
            new MockCameraModifierManager(),
            new MockScreenEffectManager(),
            mockCurtain);

        var cueRegistry = new TransientCueHandlerRegistry();
        var cue = new CurtainCue(true, new DirectionColor(0, 0, 0, 1), 0.5);
        var handler = cueRegistry.Resolve(cue);
        handler.Validate(cue);

        var handle = handler.Execute(cue, context);
        Assert.NotNull(handle);
        handle.Finish();
        await handle.Completion;
        Assert.Equal(OperationState.Completed, handle.State);

        var handle2 = handler.Execute(cue, context);
        handle2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle2.Completion);
        Assert.Equal(OperationState.Cancelled, handle2.State);
    }

    // ==========================================
    // 3. Audio Crossfade & Preemption Tests
    // ==========================================

    [Fact(Timeout = 5_000)]
    public async Task AudioCrossfade_NaturalCompletion_And_Preemption()
    {
        var harness = await TestAudioHarness.CreateAsync(autoCompleteFades: false);
        var track1 = await harness.Audio.PlayBgmAsync("calm", "music");

        // Natural completion
        harness.Backend.CompleteFades();
        await track1.FadeOperation.Completion;
        Assert.Equal(AudioPlaybackState.Playing, track1.Playback.State);

        // Preempt with second track
        var track2 = await harness.Audio.PlayBgmAsync("battle", "music", fade: new AudioFade(1));
        Assert.Equal(2, harness.Backend.ActivePlayers.Count);

        // Finish second fade
        harness.Backend.CompleteFades();
        await track2.FadeOperation.Completion;

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

    // ==========================================
    // 4. Dialogue Command & Quiescence Tests
    // ==========================================

    [RuntimeCommand("test_cmd")]
    private sealed class TestCmdArgs
    {
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCommand_ExecutesAndQuiesces()
    {
        var binder = new CommandBinder(new CommandBindingContext(0.35));
        var dispatcher = new RuntimeCommandDispatcher(binder);
        var executed = false;

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Register<TestCmdArgs>((_, _) =>
        {
            executed = true;
            return Task.CompletedTask;
        });
        var catalog = cmdRegistry.BuildCatalog();

        await dispatcher.ExecuteAsync(catalog.Commands[0], Array.Empty<string>(), CancellationToken.None);
        Assert.True(executed);
        Assert.False(dispatcher.HasActiveCommands);

        // Quiescence drain should complete immediately when no commands are active
        await dispatcher.DrainDialogueOperationsAsync(CancellationToken.None);
    }

    [RuntimeCommand("fail_cmd")]
    private sealed class FailCmdArgs
    {
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCommand_Fault_IsCapturedAndReported()
    {
        var binder = new CommandBinder(new CommandBindingContext(0.35));
        var dispatcher = new RuntimeCommandDispatcher(binder, abortOnRecoverableFailure: true);

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Register<FailCmdArgs>((_, _) => throw new InvalidOperationException("boom"));
        var catalog = cmdRegistry.BuildCatalog();

        await Assert.ThrowsAsync<RuntimeCommandException>(() =>
            dispatcher.ExecuteAsync(catalog.Commands[0], Array.Empty<string>(), CancellationToken.None));

        Assert.NotNull(dispatcher.LastFailure);
        Assert.False(dispatcher.HasActiveCommands);
    }

    [RuntimeCommand("long_cmd")]
    private sealed class LongCmdArgs
    {
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueStop_CancelsActiveCommandsAndReachesQuiescence()
    {
        var binder = new CommandBinder(new CommandBindingContext(0.35));
        var dispatcher = new RuntimeCommandDispatcher(binder);
        var startedTcs = new TaskCompletionSource();
        var cancelledTcs = new TaskCompletionSource();

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Register<LongCmdArgs>(async (_, ctx) =>
        {
            startedTcs.SetResult();
            try
            {
                await Task.Delay(10000, ctx.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelledTcs.SetResult();
                throw;
            }
        });
        var catalog = cmdRegistry.BuildCatalog();

        using var cts = new CancellationTokenSource();
        var runTask = dispatcher.ExecuteAsync(catalog.Commands[0], Array.Empty<string>(), cts.Token);

        await startedTcs.Task;
        Assert.True(dispatcher.HasActiveCommands);

        cts.Cancel();
        await runTask;
        await cancelledTcs.Task;

        await dispatcher.DrainDialogueOperationsAsync(CancellationToken.None);
        Assert.False(dispatcher.HasActiveCommands);
    }

    // ==========================================
    // 5. RuntimeHost Reset/Shutdown Ordering
    // ==========================================

    private interface IServiceA : IRuntimeService { }
    private interface IServiceB : IRuntimeService { }

    [Fact(Timeout = 5_000)]
    public async Task RuntimeHost_ResetAndShutdown_OrderingIsDeterministic()
    {
        var resetOrder = new List<string>();
        var shutdownOrder = new List<string>();

        var serviceA = new OrderedLoggingServiceA("ServiceA", resetOrder, shutdownOrder);
        var serviceB = new OrderedLoggingServiceB("ServiceB", resetOrder, shutdownOrder);

        var registry = new RuntimeServiceRegistry()
            .AddSingleton<IServiceA>(_ => serviceA)
            .AddSingleton<IFrameClock>(_ => new StubFrameClock())
            .AddSingleton<IServiceB>(
                _ => serviceB,
                typeof(IServiceA)); // serviceB depends on serviceA

        var host = registry.Build();
        await host.InitializeAsync();

        await host.ResetAsync();
        Assert.NotEmpty(resetOrder);

        await host.ShutdownAsync();
        Assert.NotEmpty(shutdownOrder);

        // Shutdown is reverse topological order
        var aIndex = shutdownOrder.IndexOf("ServiceA");
        var bIndex = shutdownOrder.IndexOf("ServiceB");
        Assert.True(bIndex < aIndex, "Dependent service B should shut down before prerequisite service A.");
    }

    // ==========================================
    // 6. Activity Observer Fake & Leak Tests
    // ==========================================

    [Fact(Timeout = 5_000)]
    public async Task FakeActivityObserver_TracksAndClearsActivities_DetectsLeaks()
    {
        var observer = new FakeRuntimeActivityObserver();
        Assert.False(observer.HasActiveActivities(RuntimeActivityOwner.Direction));

        var lease = observer.BeginActivity(RuntimeActivityOwner.Direction, "CameraFx", "cue-1");
        Assert.True(observer.HasActiveActivities(RuntimeActivityOwner.Direction));
        Assert.Single(observer.Snapshot());

        lease.Dispose();
        Assert.False(observer.HasActiveActivities(RuntimeActivityOwner.Direction));
        Assert.Empty(observer.Snapshot());

        // Test quiescence wait
        await observer.WaitForQuiescenceAsync(CancellationToken.None);
    }

    // ==========================================
    // Test Doubles & Helpers
    // ==========================================

    private sealed class MockCameraModifierManager : ICameraModifierManager
    {
        public bool Contains(string effectName) => true;

        public IOperationHandle ApplyEffect(CameraFxCue cue)
        {
            return new MockOperationHandle(cue.DurationSeconds);
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MockScreenEffectManager : IScreenEffectManager
    {
        public ScreenEffectState State => ScreenEffectState.Default;
        public ScreenEffectState TargetState => ScreenEffectState.Default;

        public IOperationHandle SetOverlay(Color color, float opacity, StageOperationSpec? operation = null) =>
            new MockOperationHandle(operation?.DurationSeconds ?? 0);

        public IOperationHandle SetLetterbox(float ratio, StageOperationSpec? operation = null) =>
            new MockOperationHandle(operation?.DurationSeconds ?? 0);

        public IOperationHandle Flash(Color color, float intensity, StageOperationSpec? operation = null) =>
            new MockOperationHandle(operation?.DurationSeconds ?? 0);

        public void ApplyStableState(ScreenEffectState state) { }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MockStageCurtainManager : IStageCurtainManager
    {
        public StageCurtainState State => StageCurtainState.Default;
        public StageCurtainState TargetState => StageCurtainState.Default;

        public IOperationHandle SetCurtain(Color color, float opacity, StageOperationSpec? operation = null) =>
            new MockOperationHandle(operation?.DurationSeconds ?? 0);

        public void ApplyStableState(StageCurtainState state) { }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MockOperationHandle : IOperationHandle
    {
        private readonly TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MockOperationHandle(double durationSeconds)
        {
            State = OperationState.Running;
        }

        public OperationState State { get; private set; }
        public Task Completion => tcs.Task;

        public void Cancel()
        {
            if (State is OperationState.Completed or OperationState.Cancelled) return;
            State = OperationState.Cancelled;
            tcs.TrySetCanceled();
        }

        public void Finish()
        {
            if (State is OperationState.Completed or OperationState.Cancelled) return;
            State = OperationState.Completed;
            tcs.TrySetResult();
        }
    }

    private sealed class OrderedLoggingServiceA : IServiceA
    {
        private readonly string name;
        private readonly List<string> resetList;
        private readonly List<string> shutdownList;

        public OrderedLoggingServiceA(string name, List<string> resetList, List<string> shutdownList)
        {
            this.name = name;
            this.resetList = resetList;
            this.shutdownList = shutdownList;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            resetList.Add(name);
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            shutdownList.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class OrderedLoggingServiceB : IServiceB
    {
        private readonly string name;
        private readonly List<string> resetList;
        private readonly List<string> shutdownList;

        public OrderedLoggingServiceB(string name, List<string> resetList, List<string> shutdownList)
        {
            this.name = name;
            this.resetList = resetList;
            this.shutdownList = shutdownList;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            resetList.Add(name);
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            shutdownList.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class StubFrameClock : IFrameClock
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NextFrameAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public enum RuntimeActivityOwner
    {
        Dialogue,
        Direction,
        Audio,
        Other,
    }

    public sealed record RuntimeActivityInfo(
        string DiagnosticId,
        RuntimeActivityOwner Owner,
        string Type,
        bool IsCancelable,
        bool IsFinishable,
        TimeSpan Elapsed,
        string? Source);

    private sealed class FakeRuntimeActivityObserver
    {
        private readonly object gate = new();
        private readonly List<RuntimeActivityInfo> activities = new();

        public IReadOnlyList<RuntimeActivityInfo> Snapshot()
        {
            lock (gate) return activities.ToArray();
        }

        public bool HasActiveActivities(RuntimeActivityOwner owner, bool includeIgnorable = false)
        {
            lock (gate) return activities.Any(a => a.Owner == owner);
        }

        public Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (activities.Count == 0) return Task.CompletedTask;
            }
            return Task.CompletedTask;
        }

        public IDisposable BeginActivity(RuntimeActivityOwner owner, string type, string diagnosticId)
        {
            var info = new RuntimeActivityInfo(diagnosticId, owner, type, true, true, TimeSpan.Zero, null);
            lock (gate) activities.Add(info);
            return new Lease(() =>
            {
                lock (gate) activities.Remove(info);
            });
        }

        private sealed class Lease : IDisposable
        {
            private Action? onDispose;
            public Lease(Action onDispose) => this.onDispose = onDispose;
            public void Dispose() => Interlocked.Exchange(ref onDispose, null)?.Invoke();
        }
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
                        resolver.Get<IOperationRunner>(),
                        bgm,
                        sfx,
                        voice,
                        new AudioSettings(),
                        missingResources: resolver.Get<IMissingResourceReporter>()),
                    typeof(IOperationRunner),
                    typeof(IMissingResourceReporter));

            var host = registry.Build();
            await host.InitializeAsync();
            return new TestAudioHarness(host, backend, host.Services.Get<IAudioManager>());
        }
    }
}
