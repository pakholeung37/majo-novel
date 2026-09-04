#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Script;
using Majo.Runtime.Stage;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class Phase1ActivityObserverTests
{
    [Fact]
    public void Registry_TracksLeases_AndCalculatesSnapshotsAccurately()
    {
        var registry = new RuntimeActivityRegistry();

        Assert.Empty(registry.Snapshot());
        Assert.False(registry.HasActiveActivities());
        Assert.False(registry.HasActiveActivities(RuntimeActivityOwner.Direction));

        var lease1 = registry.BeginActivity(RuntimeActivityOwner.Direction, "Transaction", "tx-1");
        var lease2 = registry.BeginActivity(RuntimeActivityOwner.Audio, "Voice", "voice-1");
        var lease3 = registry.BeginActivity(RuntimeActivityOwner.Dialogue, "Command", "wait", isIgnorable: true);

        Assert.True(registry.HasActiveActivities());
        Assert.True(registry.HasActiveActivities(includeIgnorable: true));
        Assert.True(registry.HasActiveActivities(RuntimeActivityOwner.Direction));
        Assert.True(registry.HasActiveActivities(
            RuntimeActivityOwner.Audio,
            includeIgnorable: true));
        Assert.False(registry.HasActiveActivities(RuntimeActivityOwner.Dialogue, includeIgnorable: false));
        Assert.True(registry.HasActiveActivities(RuntimeActivityOwner.Dialogue, includeIgnorable: true));

        var snapshot = registry.Snapshot();
        Assert.Equal(3, snapshot.Count);

        var dirInfo = snapshot.First(s => s.Owner == RuntimeActivityOwner.Direction);
        Assert.Equal("Transaction", dirInfo.Type);
        Assert.Equal("tx-1", dirInfo.Source);
        Assert.False(dirInfo.IsIgnorable);

        var dialInfo = snapshot.First(s => s.Owner == RuntimeActivityOwner.Dialogue);
        Assert.Equal("Command", dialInfo.Type);
        Assert.True(dialInfo.IsIgnorable);

        lease1.Dispose();
        Assert.False(registry.HasActiveActivities(RuntimeActivityOwner.Direction));
        Assert.Equal(2, registry.Snapshot().Count);

        lease2.Dispose();
        lease3.Dispose();
        Assert.Empty(registry.Snapshot());
        Assert.False(registry.HasActiveActivities(includeIgnorable: true));
    }

    [Fact(Timeout = 5_000)]
    public async Task Registry_Quiescence_WaitsOnlyForNonIgnorableActivities()
    {
        var registry = new RuntimeActivityRegistry();

        // 1. Initially empty -> quiescent immediately
        await registry.WaitForQuiescenceAsync();

        // 2. Ignorable activity only -> still quiescent
        var ignorableLease = registry.BeginActivity(
            RuntimeActivityOwner.Other, "Heartbeat", isIgnorable: true);
        await registry.WaitForQuiescenceAsync();

        // 3. Non-ignorable activity -> pending until disposed
        var busyLease = registry.BeginActivity(RuntimeActivityOwner.Direction, "Cutscene");
        var waitTask = registry.WaitForQuiescenceAsync();
        Assert.False(waitTask.IsCompleted);

        busyLease.Dispose();
        await waitTask;

        ignorableLease.Dispose();
        await registry.WaitForQuiescenceAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task Registry_ResetAndShutdown_WaitsForActivitiesAndPreservesLeaseOwnership()
    {
        var registry = new RuntimeActivityRegistry();

        var lease1 = registry.BeginActivity(RuntimeActivityOwner.Direction, "T1");
        var lease2 = registry.BeginActivity(RuntimeActivityOwner.Audio, "A1");

        Assert.Equal(2, registry.Snapshot().Count);
        var waitTask = registry.WaitForQuiescenceAsync();
        Assert.False(waitTask.IsCompleted);

        var resetTask = registry.ResetAsync(CancellationToken.None);
        Assert.False(resetTask.IsCompleted);
        lease1.Dispose();
        lease2.Dispose();
        await resetTask;

        Assert.Empty(registry.Snapshot());
        await waitTask;

        // Now test ShutdownAsync
        var lease3 = registry.BeginActivity(RuntimeActivityOwner.Dialogue, "D1");
        var shutdownWait = registry.WaitForQuiescenceAsync();
        Assert.False(shutdownWait.IsCompleted);

        var shutdownTask = registry.ShutdownAsync(CancellationToken.None);
        Assert.False(shutdownTask.IsCompleted);
        lease3.Dispose();
        await shutdownTask;

        Assert.Empty(registry.Snapshot());
        await shutdownWait;
    }

    [Fact(Timeout = 5_000)]
    public async Task DirectionTransaction_RegistersAndCleansUpActivityLease()
    {
        var registry = new RuntimeActivityRegistry();
        var harness = new ComponentBackendHarness();
        var backend = new CompositeDirectionPreparationBackend(harness, harness, harness, harness);
        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            activities: registry);

        var planDirectives = new IDirectionDirective[]
        {
            new StageCreatePatch(StageId.Main, ViewId.Main, DirectionTransitionSpec.Cut),
            new BackdropPatch(StageId.Main, new BackdropState(new ResourceKey("bg1")), new DirectionTransitionSpec(DirectionTransitionKind.Fade, 1.0)),
        };

        var tx = await coordinator.ExecuteAsync(planDirectives, DirectionCompletionBarrier.Commit);

        // While transaction is active, registry must record Direction activity
        Assert.True(registry.HasActiveActivities(RuntimeActivityOwner.Direction));
        var snapshot = registry.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(RuntimeActivityOwner.Direction, snapshot[0].Owner);
        Assert.Equal("Transaction", snapshot[0].Type);

        // After transaction finishes/settles, activity must be released
        await coordinator.FinishAsync(tx);
        await tx.Settlement;

        Assert.False(registry.HasActiveActivities(RuntimeActivityOwner.Direction));
        Assert.Empty(registry.Snapshot());
    }

    [Fact(Timeout = 5_000)]
    public async Task AudioManager_VoicePlayback_TracksVoiceActivityInRegistry()
    {
        var registry = new RuntimeActivityRegistry();
        var backend = new FakeAudioBackend { AutoCompleteFades = true };
        var clock = new StubFrameClock();
        var lifetime = new FakeLifetime();
        var scheduler = new OperationRunner(clock, lifetime);
        var bgmLoader = new FakeAudioClipLoader("audio/bgm").Register("bgm1");
        var sfxLoader = new FakeAudioClipLoader("audio/sfx").Register("sfx1");
        var voiceLoader = new FakeAudioClipLoader("audio/voice").Register("v_hello").Register("en/v_hello");

        var audio = new AudioManager(
            backend,
            bgmLoader,
            sfxLoader,
            voiceLoader,
            new AudioSettings(),
            activities: registry);

        await audio.InitializeAsync(CancellationToken.None);

        var result = await audio.PlayVoiceAsync("v_hello", cancellationToken: CancellationToken.None);
        Assert.NotNull(result.Playback);

        // Registry should show Audio Voice activity
        Assert.True(registry.HasActiveActivities(
            RuntimeActivityOwner.Audio,
            includeIgnorable: true));
        var snap = registry.Snapshot();
        Assert.Contains(snap, a => a.Owner == RuntimeActivityOwner.Audio && a.Type == "Voice");

        // Stopping voice should release the activity
        await audio.StopVoiceAsync(cancellationToken: CancellationToken.None);

        Assert.False(registry.HasActiveActivities(RuntimeActivityOwner.Audio));
        Assert.Empty(registry.Snapshot());
    }

    [RuntimeCommand("p1_test_cmd")]
    private sealed class P1TestCmdArgs
    {
    }

    [Fact(Timeout = 5_000)]
    public async Task DialogueCommandDispatcher_TracksCommandActivityInRegistry()
    {
        var registry = new RuntimeActivityRegistry();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cmdRegistry = new RuntimeCommandRegistry();
        cmdRegistry.Register<P1TestCmdArgs>((_, _) => tcs.Task);
        var catalog = cmdRegistry.BuildCatalog();

        var binder = new CommandBinder(new CommandBindingContext(0.35));
        var dispatcher = new RuntimeCommandDispatcher(
            binder,
            activities: registry);

        var reg = catalog.Commands.First();
        var execTask = dispatcher.ExecuteAsync(reg, Array.Empty<string>(), CancellationToken.None);

        // While executing, Dialogue activity is present
        Assert.True(registry.HasActiveActivities(RuntimeActivityOwner.Dialogue));
        var snap = registry.Snapshot();
        Assert.Single(snap);
        Assert.Equal("Command", snap[0].Type);
        Assert.Equal("p1_test_cmd", snap[0].Source);

        // Complete command
        tcs.SetResult();
        await execTask;

        // Activity lease must be disposed
        Assert.False(registry.HasActiveActivities(RuntimeActivityOwner.Dialogue));
        Assert.Empty(registry.Snapshot());
    }

    [Fact(Timeout = 5_000)]
    public async Task StateEligibility_RejectsSaveWhenNonIgnorableActivityIsActive()
    {
        var registry = new RuntimeActivityRegistry();
        var script = new FakeScriptPlayback { PhaseValue = ScriptPlaybackPhase.AwaitingAdvance };
        var input = new FakeInputManager();

        var eligibility = new StateEligibility(script, registry, input);
        await eligibility.InitializeAsync(CancellationToken.None);

        // Initially allowed
        var res1 = eligibility.Check(StateOperationKind.Save);
        Assert.True(res1.IsAllowed);

        // Begin non-ignorable activity
        using var lease = registry.BeginActivity(RuntimeActivityOwner.Direction, "Transaction");
        var res2 = eligibility.Check(StateOperationKind.Save);
        Assert.False(res2.IsAllowed);
        Assert.Contains(res2.Reasons, r => r.Code == StateEligibilityReasonCode.ActiveOperation);

        // After lease disposed -> allowed again
        lease.Dispose();
        var res3 = eligibility.Check(StateOperationKind.Save);
        Assert.True(res3.IsAllowed);
    }

    [Fact(Timeout = 5_000)]
    public async Task Diagnostics_PopulatesFromRuntimeActivityObserver()
    {
        var registry = new RuntimeActivityRegistry();
        var services = new TestServiceResolver();
        services.Register<IRuntimeActivityObserver>(registry);

        var diagnostics = new RuntimeDiagnosticsService(services);
        await diagnostics.InitializeAsync(CancellationToken.None);

        using var lease = registry.BeginActivity(RuntimeActivityOwner.Audio, "Fade", "bgm_intro");

        var snapshot = diagnostics.CaptureSnapshot();
        Assert.NotNull(snapshot.Activities);
        Assert.Equal(1, snapshot.Activities!.ActiveCount);
        var act = Assert.Single(snapshot.Activities.ActiveActivities);
        Assert.Equal(RuntimeActivityOwner.Audio, act.Owner);
        Assert.Equal("Fade", act.Type);
        Assert.Equal("bgm_intro", act.Source);
    }

    private sealed class FakeLifetime : IRuntimeLifetime
    {
        public string RuntimeId => "test-runtime";
        public CancellationToken RuntimeCancellationToken => CancellationToken.None;
        public CancellationToken SessionCancellationToken => CancellationToken.None;
    }

    private sealed class FakeScriptPlayback : IScriptPlayback
    {
        public event Action<ScriptPlaybackPhaseChanged>? PhaseChanged { add { } remove { } }
        public ScriptPlaybackPhase PhaseValue { get; set; } = ScriptPlaybackPhase.Idle;
        public ScriptPlaybackPhase Phase => PhaseValue;
        public ScriptPlaybackSpot? CurrentSpot => null;
        public bool CanCaptureCheckpoint => Phase == ScriptPlaybackPhase.AwaitingAdvance;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ScriptCheckpoint CaptureCheckpoint() => throw new NotSupportedException();
        public Task ValidateCheckpointAsync(ScriptCheckpoint checkpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreCheckpointAsync(ScriptCheckpoint checkpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeInputManager : IInputManager
    {
        public event Action<InputActivation>? Dispatched { add { } remove { } }
        public event Action? HandlesChanged { add { } remove { } }
        public bool Enabled { get; set; } = true;
        public int ActiveHandleCount => 0;
        public IReadOnlyList<InputHandleSnapshot> ActiveHandles => Array.Empty<InputHandleSnapshot>();
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IInputHandle AcquireScope(object owner, InputScopeOptions? options = null) => throw new NotSupportedException();
        public IInputHandle AcquireMuter(object owner, InputMuteOptions? options = null) => throw new NotSupportedException();
        public void ReleaseOwner(object owner) { }
    }

    private sealed class TestServiceResolver : IServiceResolver
    {
        private readonly Dictionary<Type, object> map = new();

        public void Register<T>(T instance) where T : class
        {
            map[typeof(T)] = instance;
        }

        public TService Resolve<TService>() where TService : class => (TService)map[typeof(TService)];

        public object Resolve(Type serviceType) => map[serviceType];

        public bool TryGet<T>(out T? service) where T : class
        {
            if (map.TryGetValue(typeof(T), out var obj))
            {
                service = (T)obj;
                return true;
            }
            service = null;
            return false;
        }
    }

    private sealed class StubFrameClock : IFrameClock
    {
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NextFrameAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
