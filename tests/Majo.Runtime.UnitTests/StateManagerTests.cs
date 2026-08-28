using System.Text.Json;
using Majo.Runtime.Actors;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class StateManagerTests
{
    [Fact]
    public void RegistryBuildsStableTopologicalOrderAndRejectsInvalidGraphs()
    {
        var events = new List<string>();
        var variables = new RecordingContributor("variables", [], events);
        var actors = new RecordingContributor("actors", ["variables"], events);
        var script = new RecordingContributor(
            "script",
            ["variables", "actors"],
            events);

        var ordered = new StateContributorRegistry()
            .Register(script)
            .Register(actors)
            .Register(variables)
            .Build();

        Assert.Equal(["variables", "actors", "script"], ordered.Select(item => item.Key));
        Assert.Throws<DuplicateStateContributorException>(() =>
            new StateContributorRegistry().Register(variables).Register(variables));
        Assert.Throws<StateRuntimeException>(() =>
            new StateContributorRegistry()
                .Register(new RecordingContributor("orphan", ["missing"], events))
                .Build());
        Assert.Throws<StateRuntimeException>(() =>
            new StateContributorRegistry()
                .Register(new RecordingContributor("a", ["b"], events))
                .Register(new RecordingContributor("b", ["a"], events))
                .Build());
    }

    [Fact]
    public async Task RestorePreflightsEverythingThenResetsAndRestoresInDependencyOrder()
    {
        var events = new List<string>();
        var variables = new RecordingContributor("variables", [], events);
        var actors = new RecordingContributor("actors", ["variables"], events);
        var script = new RecordingContributor(
            "script",
            ["variables", "actors"],
            events);
        var manager = CreateManager(
            new StateContributorRegistry()
                .Register(script)
                .Register(actors)
                .Register(variables)
                .Build(),
            events);
        await manager.InitializeAsync(CancellationToken.None);
        var snapshot = await manager.CaptureGameAsync();
        events.Clear();

        await manager.RestoreGameAsync(snapshot);

        Assert.Equal(
            [
                "preflight:variables",
                "preflight:actors",
                "preflight:script",
                "input:acquire",
                "dialogue:stop",
                "reset:script",
                "reset:actors",
                "reset:variables",
                "restore:variables",
                "restore:actors",
                "restore:script",
                "input:dispose",
            ],
            events);
        Assert.Equal(StateManagerStatus.Idle, manager.Status);
        Assert.Same(snapshot, manager.CurrentSnapshot);
    }

    [Fact]
    public async Task PreflightFailureDoesNotStopOrMutateTheCurrentGame()
    {
        var events = new List<string>();
        var variables = new RecordingContributor("variables", [], events);
        var script = new RecordingContributor("script", ["variables"], events);
        var manager = CreateManager(
            new StateContributorRegistry()
                .Register(script)
                .Register(variables)
                .Build(),
            events);
        await manager.InitializeAsync(CancellationToken.None);
        var snapshot = await manager.CaptureGameAsync();
        script.FailPreflight = true;
        events.Clear();

        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            manager.RestoreGameAsync(snapshot));

        Assert.Equal(
            ["preflight:variables", "preflight:script"],
            events);
        Assert.Equal(StateManagerStatus.Idle, manager.Status);
    }

    [Fact]
    public async Task RestoreFailureKeepsInputMutedUntilRuntimeReset()
    {
        var events = new List<string>();
        var variables = new RecordingContributor("variables", [], events);
        var script = new RecordingContributor("script", ["variables"], events);
        var manager = CreateManager(
            new StateContributorRegistry().Register(script).Register(variables).Build(),
            events);
        await manager.InitializeAsync(CancellationToken.None);
        var snapshot = await manager.CaptureGameAsync();
        script.FailRestore = true;
        events.Clear();

        await Assert.ThrowsAsync<StateRuntimeException>(() =>
            manager.RestoreGameAsync(snapshot));

        Assert.Equal(StateManagerStatus.Faulted, manager.Status);
        Assert.DoesNotContain("input:dispose", events);

        await manager.ResetAsync(CancellationToken.None);

        Assert.Equal("input:dispose", events[^1]);
        Assert.Equal(StateManagerStatus.Idle, manager.Status);
    }

    [Fact]
    public async Task RestoreRejectsMissingRequiredRecordsButIgnoresUnknownLegacyRecords()
    {
        var events = new List<string>();
        var only = new RecordingContributor("only", [], events);
        var manager = CreateManager(
            new StateContributorRegistry().Register(only).Build(),
            events);
        await manager.InitializeAsync(CancellationToken.None);
        var missing = new StateSnapshot(StateSnapshot.CurrentFormatVersion, DateTimeOffset.UtcNow, []);

        await Assert.ThrowsAsync<StateCompatibilityException>(() =>
            manager.RestoreGameAsync(missing));
        Assert.Empty(events);

        await manager.ResetAsync(CancellationToken.None);
        var unknown = new StateSnapshot(
            StateSnapshot.CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            [
                await only.CaptureAsync(CancellationToken.None),
                new StateRecord(
                    "unknown",
                    StateScope.Game,
                    1,
                    JsonSerializer.SerializeToElement("unknown")),
            ]);
        events.Clear();

        await manager.RestoreGameAsync(unknown);
        Assert.Equal(
            [
                "preflight:only",
                "input:acquire",
                "dialogue:stop",
                "reset:only",
                "restore:only",
                "input:dispose",
            ],
            events);
        Assert.Equal(["ignored:unknown@1"], manager.LastMigrationChain);
    }

    [Fact]
    public async Task RuntimeResetOnlyResetsSessionContributors()
    {
        var events = new List<string>();
        var session = new RecordingContributor(
            "history",
            [],
            events,
            StateScope.Session);
        var game = new RecordingContributor("game", [], events);
        var manager = CreateManager(
            new StateContributorRegistry().Register(game).Register(session).Build(),
            events);
        await manager.InitializeAsync(CancellationToken.None);

        await manager.ResetAsync(CancellationToken.None);

        Assert.Equal(["reset:history"], events);
    }

    [Fact]
    public async Task OptionalContributorUsesDefaultWhenOldSnapshotHasNoRecord()
    {
        var events = new List<string>();
        var optional = new RecordingContributor(
            "optional",
            [],
            events,
            capability: new StateContributorCapability(
                true,
                StateRollbackPolicy.Supported,
                StateRestoreRecordPolicy.UseDefault));
        var manager = CreateManager(
            new StateContributorRegistry().Register(optional).Build(),
            events);
        await manager.InitializeAsync(CancellationToken.None);
        var oldSnapshot = new StateSnapshot(
            StateSnapshot.CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            []);

        await manager.RestoreGameAsync(oldSnapshot);

        Assert.Equal(
            ["input:acquire", "dialogue:stop", "reset:optional", "input:dispose"],
            events);
    }

    [Fact]
    public async Task ExplicitKeyMigrationAndRemovedContributorAreDeterministic()
    {
        var events = new List<string>();
        var current = new RecordingContributor("current", [], events);
        var migrations = new StateMigrationRegistry()
            .RegisterContributor(new DelegateStateContributorMigration(
                "rename-legacy-to-current",
                "legacy",
                1,
                "current",
                1,
                payload => payload))
            .RegisterRemovedContributor("removed");
        var manager = CreateManager(
            new StateContributorRegistry().Register(current).Build(),
            events,
            migrations);
        await manager.InitializeAsync(CancellationToken.None);
        var snapshot = new StateSnapshot(
            StateSnapshot.CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            [
                new StateRecord(
                    "legacy",
                    StateScope.Game,
                    1,
                    JsonSerializer.SerializeToElement("legacy-value")),
                new StateRecord(
                    "removed",
                    StateScope.Game,
                    4,
                    JsonSerializer.SerializeToElement("removed-value")),
            ]);

        await manager.RestoreGameAsync(snapshot);

        Assert.Equal(
            ["rename-legacy-to-current", "removed:removed@4"],
            manager.LastMigrationChain);
        Assert.Equal(
            [
                "preflight:current",
                "input:acquire",
                "dialogue:stop",
                "reset:current",
                "restore:current",
                "input:dispose",
            ],
            events);
    }

    [Fact]
    public async Task CaptureAndRestoreRecheckEligibilityAfterCallerPreflight()
    {
        var events = new List<string>();
        var contributor = new RecordingContributor("game", [], events);
        var eligibility = new RecordingEligibility();
        var manager = CreateManager(
            new StateContributorRegistry().Register(contributor).Build(),
            events,
            eligibility: eligibility);
        await manager.InitializeAsync(CancellationToken.None);
        var snapshot = await manager.CaptureGameAsync();
        events.Clear();

        Assert.True(eligibility.Check(StateOperationKind.Save).IsAllowed);
        eligibility.Deny = true;
        await Assert.ThrowsAsync<StateEligibilityException>(() =>
            manager.CaptureGameAsync());
        Assert.Empty(events);

        eligibility.Deny = false;
        var plan = await manager.RestoreTransaction.PreflightAsync(
            snapshot,
            StateScope.Game);
        events.Clear();
        eligibility.Deny = true;
        await Assert.ThrowsAsync<StateEligibilityException>(() =>
            manager.RestoreTransaction.ExecuteAsync(plan));
        Assert.Empty(events);
        Assert.Equal(StateManagerStatus.Idle, manager.Status);
    }

    [Fact(Timeout = 5_000)]
    public async Task ActorStateContributorPreservesVisualCreationOrder()
    {
        var factory = new FakeActorFactory("test");
        var definitions = new[]
        {
            new ActorDefinition("stills", "test", "default", []),
            new ActorDefinition("scenes", "test", "default", []),
            new ActorDefinition("hero", "test", "default", []),
        };
        var manager = new ActorManager(definitions, [factory]);
        await manager.InitializeAsync(CancellationToken.None);

        manager.Create("stills").SetZIndex(-10);
        manager.Create("scenes");
        manager.Create("hero").SetZIndex(20);

        var contributor = new ActorStateContributor(manager);
        var record = await contributor.CaptureAsync(CancellationToken.None);

        // Reset and verify order is restored as stills -> scenes -> hero (not alphabetical scenes -> stills -> hero)
        await manager.ResetAsync(CancellationToken.None);
        Assert.Empty(manager.Actors);

        await contributor.RestoreAsync(record, CancellationToken.None);
        Assert.Equal(["stills", "scenes", "hero"], manager.Actors.Select(a => a.Id));
        Assert.Equal([-10, 0, 20], manager.Actors.Select(a => a.ZIndex));

        await manager.ShutdownAsync(CancellationToken.None);
    }

    private sealed class FakeActorFactory : IActorFactory
    {
        public FakeActorFactory(string factoryId) => FactoryId = factoryId;
        public string FactoryId { get; }
        public void ValidateDefinition(ActorDefinition definition) { }
        public IActor Create(ActorDefinition definition) => new FakeActor(definition.Id, definition.DefaultAppearance);
        public IActor Create(ActorDefinition definition, ActorCreationMode mode) =>
            new FakeActor(definition.Id, definition.DefaultAppearance, mode == ActorCreationMode.Active);
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id, string appearance, bool visible = true)
        {
            Id = id;
            Appearance = TargetAppearance = appearance;
            Visible = TargetVisible = visible;
        }

        public string Id { get; }
        public string FactoryId => "test";
        public string Appearance { get; set; }
        public string TargetAppearance { get; }
        public bool Visible { get; set; }
        public bool TargetVisible { get; }
        public Godot.Vector2 Position => Godot.Vector2.Zero;
        public Godot.Vector2 TargetPosition => Godot.Vector2.Zero;
        public Godot.Color Tint => Godot.Colors.White;
        public float Opacity { get; set; } = 1f;
        public float TargetOpacity => 1f;
        public int ZIndex { get; private set; }

        public void SetAppearance(string appearance) => Appearance = appearance;
        public Task PrepareAppearanceAsync(string appearance, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetVisible(bool visible) => Visible = visible;
        public void SetPosition(Godot.Vector2 position) { }
        public void SetTint(Godot.Color tint) { }
        public void SetOpacity(float opacity) => Opacity = opacity;
        public void SetZIndex(int zIndex)
        {
            ActorZIndexRange.Validate(zIndex);
            ZIndex = zIndex;
        }
        public IOperationHandle ChangePosition(Godot.Vector2 position, double durationSeconds, string? operationId = null, string? groupId = null) => throw new NotImplementedException();
        public IOperationHandle ChangePosition(Godot.Vector2 position, AnimationSpec animation, string? operationId = null, string? groupId = null) => throw new NotImplementedException();
        public IOperationHandle ChangeOpacity(float opacity, double durationSeconds, string? operationId = null, string? groupId = null) => throw new NotImplementedException();
        public IOperationHandle ChangeOpacity(float opacity, AnimationSpec animation, string? operationId = null, string? groupId = null) => throw new NotImplementedException();
        public IOperationHandle Show(double durationSeconds, string? operationId = null, string? groupId = null) => throw new NotImplementedException();
        public IOperationHandle Show(AnimationSpec animation, string? operationId = null, string? groupId = null) => throw new NotImplementedException();
        public IOperationHandle Hide(double durationSeconds, string? operationId = null, string? groupId = null) => throw new NotImplementedException();
        public IOperationHandle Hide(AnimationSpec animation, string? operationId = null, string? groupId = null) => throw new NotImplementedException();
        public void Destroy() { }
    }

    private static StateManager CreateManager(
        IReadOnlyList<IStateContributor> contributors,
        IList<string> events,
        StateMigrationRegistry? migrations = null,
        IStateEligibility? eligibility = null) => new(
        contributors,
        new StubDialoguePlayer(events),
        new StubInputManager(events),
        new StubOperationScheduler(),
        eligibility,
        migrations: migrations);

    private sealed class RecordingContributor : IStateContributor
    {
        private readonly IList<string> events;

        public RecordingContributor(
            string key,
            IReadOnlyCollection<string> dependencies,
            IList<string> events,
            StateScope scope = StateScope.Game,
            int schemaVersion = 1,
            StateContributorCapability? capability = null)
        {
            Key = key;
            Dependencies = dependencies;
            this.events = events;
            Scope = scope;
            SchemaVersion = schemaVersion;
            Capability = capability ?? (scope == StateScope.Game
                ? StateContributorCapability.DurableRollback
                : StateContributorCapability.Transient);
        }

        public string Key { get; }

        public StateScope Scope { get; }

        public int SchemaVersion { get; }

        public StateContributorCapability Capability { get; }

        public IReadOnlyCollection<string> Dependencies { get; }

        public bool FailPreflight { get; set; }

        public bool FailRestore { get; set; }

        public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"capture:{Key}");
            return ValueTask.FromResult(new StateRecord(
                Key,
                Scope,
                SchemaVersion,
                JsonSerializer.SerializeToElement(Key)));
        }

        public ValueTask PreflightAsync(
            StateRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"preflight:{Key}");
            if (FailPreflight)
            {
                throw new StateSnapshotException($"Preflight failed for '{Key}'.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreAsync(
            StateRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"restore:{Key}");
            if (FailRestore)
            {
                throw new StateRuntimeException($"Restore failed for '{Key}'.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"reset:{Key}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubDialoguePlayer : IDialoguePlayer
    {
        private readonly IList<string> events;

        public StubDialoguePlayer(IList<string> events)
        {
            this.events = events;
        }

        public event Action<DialogueStateChanged>? StateChanged { add { } remove { } }
        public event Action<DialogueStartRequest>? Started { add { } remove { } }
        public event Action<DialogueLine>? LinePresented { add { } remove { } }
        public event Action<IReadOnlyList<DialogueOption>>? OptionsPresented { add { } remove { } }
        public event Action<DialogueCompletion>? Completed { add { } remove { } }
        public event Action<DialogueCompletion>? Stopped { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }

        public DialogueSessionState State => DialogueSessionState.Idle;
        public string? CurrentProjectId => null;
        public string? CurrentNode => null;
        public string? CurrentLineId => null;
        public bool IsPresentingOptions => false;
        public IReadOnlyList<DialogueOption> CurrentOptions => [];
        public Task<DialogueCompletion> Completion => Task.FromResult(
            new DialogueCompletion(DialogueCompletionKind.Completed, "", ""));

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(
            DialogueStartRequest? request = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            events.Add("dialogue:stop");
            return Task.CompletedTask;
        }

        public void RequestHurryUp() { }
        public void RequestNextLine() { }
    }

    private sealed class StubInputManager : IInputManager
    {
        private readonly IList<string> events;

        public StubInputManager(IList<string> events)
        {
            this.events = events;
        }

        public event Action<InputActivation>? Dispatched { add { } remove { } }

        public bool Enabled { get; set; } = true;
        public int ActiveHandleCount => 0;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IInputHandle AcquireScope(object owner, InputScopeOptions? options = null) =>
            throw new NotSupportedException();

        public IInputHandle AcquireMuter(object owner, InputMuteOptions? options = null)
        {
            events.Add("input:acquire");
            return new StubInputHandle(owner, options?.Name, events);
        }

        public void ReleaseOwner(object owner) { }
    }

    private sealed class StubInputHandle : IInputHandle
    {
        private readonly IList<string> events;

        public StubInputHandle(object owner, string? name, IList<string> events)
        {
            Owner = owner;
            Name = name;
            this.events = events;
        }

        public event Action<InputActivation>? Activated { add { } remove { } }

        public object Owner { get; }
        public InputHandleKind Kind => InputHandleKind.Muter;
        public string? Name { get; }
        public bool IsModal => true;
        public bool Enabled { get; set; } = true;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            events.Add("input:dispose");
        }
    }

    private sealed class StubOperationScheduler : IOperationScheduler
    {
        public IReadOnlyCollection<IOperationHandle> ActiveOperations => [];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public IOperationHandle Start(OperationDefinition definition) =>
            throw new NotSupportedException();
        public Task WaitAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CancelAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CompleteAsync(
            OperationTarget target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingEligibility : IStateEligibility
    {
        public bool Deny { get; set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public StateEligibilityResult Check(StateOperationKind operation) => new(
            operation,
            Deny
                ? [new StateEligibilityReason(
                    StateEligibilityReasonCode.ExternalBarrier,
                    "State changed after the caller checked eligibility.")]
                : []);
        public IStateBarrierHandle AcquireBarrier(
            object owner,
            string reasonCode,
            StateOperationMask operations = StateOperationMask.All,
            string? diagnostic = null,
            string? playerMessage = null) => throw new NotSupportedException();
    }
}
