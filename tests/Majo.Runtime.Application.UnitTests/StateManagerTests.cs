using System.Text.Json;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Input;
using Majo.Runtime.State;
using Majo.Runtime.Application.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.Application.UnitTests;

public sealed class StateManagerTests
{
    [Fact]
    public void ManagerSuppliesTransactionContextThroughTheEligibilityCapability()
    {
        var eligibility = new RecordingEligibility();
        using var manager = CreateManager([], [], eligibility: eligibility);
        Assert.NotNull(eligibility.StatusProvider);
        Assert.Equal(StateManagerStatus.Idle, eligibility.StatusProvider!());
        Assert.Empty(eligibility.AttachedContributors!);
    }

    [Fact]
    public async Task InitializationRejectsGameContributorWithoutMutationContract()
    {
        var events = new List<string>();
        var manager = CreateManager([new UnsafePluginContributor()], events);

        var exception = await Assert.ThrowsAsync<StateCompatibilityException>(() =>
            manager.InitializeAsync(CancellationToken.None));

        Assert.Contains(nameof(IStateMutationParticipant), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestorePreparesEverythingThenCommitsInDependencyOrderWithoutReset()
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
        var snapshot = await CaptureSnapshotAsync(variables, actors, script);
        events.Clear();

        await manager.RestoreGameAsync(snapshot);

        Assert.Equal(
            [
                "preflight:variables",
                "preflight:actors",
                "preflight:script",
                "input:acquire",
                "dialogue:stop",
                "prepare:variables",
                "prepare:actors",
                "prepare:script",
                "restore:variables",
                "restore:actors",
                "restore:script",
                "dispose:script",
                "dispose:actors",
                "dispose:variables",
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
        var snapshot = await CaptureSnapshotAsync(variables, script);
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
        var snapshot = await CaptureSnapshotAsync(variables, script);
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
                "prepare:only",
                "restore:only",
                "dispose:only",
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
            ["input:acquire", "dialogue:stop", "prepare:optional", "restore:optional", "dispose:optional", "input:dispose"],
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
                "prepare:current",
                "restore:current",
                "dispose:current",
                "input:dispose",
            ],
            events);
    }

    [Fact]
    public async Task GameCaptureRequiresBoundaryAndRestoreRechecksEligibilityAfterPreflight()
    {
        var events = new List<string>();
        var contributor = new RecordingContributor("game", [], events);
        var eligibility = new RecordingEligibility();
        var manager = CreateManager(
            new StateContributorRegistry().Register(contributor).Build(),
            events,
            eligibility: eligibility);
        await manager.InitializeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            manager.CaptureGameAsync());
        Assert.Empty(events);

        var snapshot = await CaptureSnapshotAsync(contributor);
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

    private static StateManager CreateManager(
        IReadOnlyList<IStateContributor> contributors,
        IList<string> events,
        StateMigrationRegistry? migrations = null,
        IStateEligibility? eligibility = null) => new(
        contributors,
        new StubInputManager(events),
        new StubRestoreBoundary(events),
        eligibility,
        migrations: migrations);

    private static async Task<StateSnapshot> CaptureSnapshotAsync(
        params IStateContributor[] contributors) => new(
        StateSnapshot.CurrentFormatVersion,
        DateTimeOffset.UtcNow,
        await Task.WhenAll(contributors.Select(async contributor =>
            await contributor.CaptureAsync(CancellationToken.None))));

    private sealed class RecordingContributor : IPreparedStateContributor, IStateMutationParticipant
    {
        public void BindStateMutationGate(StateMutationGate gate) { }
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

        public ValueTask<IPreparedStateRestore> PrepareRestoreAsync(
            StateRecord? record, RestoreContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"prepare:{Key}");
            return ValueTask.FromResult<IPreparedStateRestore>(new PreparedStateRestore(() =>
            {
                events.Add($"restore:{Key}");
                if (FailRestore) throw new StateRuntimeException($"Restore failed for '{Key}'.");
            }, () =>
            {
                events.Add($"dispose:{Key}");
                return ValueTask.CompletedTask;
            }));
        }

        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"reset:{Key}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnsafePluginContributor : IPreparedStateContributor
    {
        public string Key => "plugin.unsafe";
        public StateScope Scope => StateScope.Game;
        public int SchemaVersion => 1;
        public StateContributorCapability Capability => StateContributorCapability.DurableRollback;
        public ValueTask<StateRecord> CaptureAsync(CancellationToken token) =>
            ValueTask.FromResult(new StateRecord(Key, Scope, SchemaVersion,
                JsonSerializer.SerializeToElement(0)));
        public ValueTask PreflightAsync(StateRecord record, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ResetAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<IPreparedStateRestore> PrepareRestoreAsync(
            StateRecord? record, RestoreContext context, CancellationToken token) =>
            ValueTask.FromResult<IPreparedStateRestore>(new PreparedStateRestore(() => { }));
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
        public event Action? HandlesChanged { add { } remove { } }

        public bool Enabled { get; set; } = true;
        public int ActiveHandleCount => 0;
        public IReadOnlyList<InputHandleSnapshot> ActiveHandles => [];

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

    private sealed class StubRestoreBoundary : IRuntimeRestoreBoundary
    {
        private readonly IList<string> events;

        public StubRestoreBoundary(IList<string> events) => this.events = events;

        public Task CancelForRestoreAsync(CancellationToken cancellationToken = default)
        {
            events.Add("dialogue:stop");
            return Task.CompletedTask;
        }

        public Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingEligibility : IStateEligibility, IStateEligibilityContextReceiver
    {
        public bool Deny { get; set; }
        public Func<StateManagerStatus>? StatusProvider { get; private set; }
        public IReadOnlyList<IStateContributor>? AttachedContributors { get; private set; }
        public void Attach(Func<StateManagerStatus> transactionStatusProvider, IReadOnlyList<IStateContributor> stateContributors,
            Func<bool>? committedBoundaryProvider = null)
        {
            StatusProvider = transactionStatusProvider;
            AttachedContributors = stateContributors;
        }
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
