using System.Text.Json;
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
        Assert.Equal(StateManagerStatus.Faulted, manager.Status);
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
    public async Task RestoreRejectsMissingAndUnknownRecordsBeforePreflight()
    {
        var events = new List<string>();
        var only = new RecordingContributor("only", [], events);
        var manager = CreateManager(
            new StateContributorRegistry().Register(only).Build(),
            events);
        await manager.InitializeAsync(CancellationToken.None);
        var missing = new StateSnapshot(StateSnapshot.CurrentFormatVersion, DateTimeOffset.UtcNow, []);

        await Assert.ThrowsAsync<StateSnapshotException>(() =>
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

        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            manager.RestoreGameAsync(unknown));
        Assert.Empty(events);
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

    private static StateManager CreateManager(
        IReadOnlyList<IStateContributor> contributors,
        IList<string> events) => new(
        contributors,
        new StubDialoguePlayer(events),
        new StubInputManager(events),
        new StubOperationScheduler());

    private sealed class RecordingContributor : IStateContributor
    {
        private readonly IList<string> events;

        public RecordingContributor(
            string key,
            IReadOnlyCollection<string> dependencies,
            IList<string> events,
            StateScope scope = StateScope.Game)
        {
            Key = key;
            Dependencies = dependencies;
            this.events = events;
            Scope = scope;
        }

        public string Key { get; }

        public StateScope Scope { get; }

        public int SchemaVersion => 1;

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
}
