using System.Text.Json;
using Majo.Runtime.Core;
using Majo.Runtime.Input;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class PreparedStateRestoreTests
{
    [Fact]
    public async Task OutputIsRetainedBeforeStopAndReleasedOnlyAfterTargetActivation()
    {
        var probe = new Probe("game");
        var (manager, _, boundary) = await CreateAsync(probe);
        boundary.Output.OnPresent = () =>
        {
            Assert.Equal(9, probe.LiveValue);
            Assert.False(probe.Activated);
            Assert.Equal(0, boundary.Output.Disposals);
        };
        manager.Restored += _ =>
        {
            Assert.True(probe.Activated);
            Assert.Equal(1, boundary.Output.Disposals);
        };
        await manager.RestoreGameAsync(Target(probe));
        Assert.Equal(1, boundary.Output.Presentations);
        Assert.Equal(1, boundary.Output.Disposals);
    }

    [Fact]
    public async Task RendererFailureKeepsFrozenOutputUntilFaultReset()
    {
        var probe = new Probe("game");
        var (manager, _, boundary) = await CreateAsync(probe);
        boundary.Output.OnPresent = () => throw new InvalidOperationException("renderer failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreGameAsync(Target(probe)));
        Assert.Equal(StateManagerStatus.Faulted, manager.Status);
        Assert.False(probe.Activated);
        Assert.Equal(0, boundary.Output.Disposals);
        await manager.ResetAsync(CancellationToken.None);
        Assert.Equal(1, boundary.Output.Disposals);
    }

    [Fact]
    public async Task PrepareFailureLeavesLiveStateAndSnapshotUntouchedAndReleasesCandidatesOnce()
    {
        var first = new Probe("first");
        var second = new Probe("second") { FailPrepare = true };
        var (manager, input, boundary) = await CreateAsync(first, second);
        var completed = 0;
        manager.Restored += _ => completed++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreGameAsync(Target(first, second)));
        Assert.Equal(1, first.LiveValue);
        Assert.Equal(1, second.LiveValue);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, second.Disposals);
        Assert.Null(manager.CurrentSnapshot);
        Assert.Equal(1, boundary.Stops);
        Assert.Equal(0, completed);
        Assert.Equal(0, input.ActiveHandleCount);
        Assert.Equal(StateManagerStatus.Idle, manager.Status);
    }

    [Fact]
    public async Task CancellationBeforeCommitDoesNotPublishAnyDomain()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new Probe("first");
        var second = new Probe("second") { Preparing = () => cancellation.Cancel() };
        var (manager, input, _) = await CreateAsync(first, second);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.RestoreGameAsync(Target(first, second), cancellation.Token));
        Assert.Equal(1, first.LiveValue);
        Assert.Equal(1, second.LiveValue);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, input.ActiveHandleCount);
        Assert.Equal(StateManagerStatus.Idle, manager.Status);
    }

    [Fact]
    public async Task CommitFailureIsFaultedWithoutSuccessAndRetainsInputLock()
    {
        var first = new Probe("first");
        var second = new Probe("second") { FailCommit = true };
        var (manager, input, _) = await CreateAsync(first, second);
        var completed = 0;
        manager.Restored += _ => completed++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreGameAsync(Target(first, second)));
        Assert.Equal(9, first.LiveValue);
        Assert.Equal(0, completed);
        Assert.False(first.Activated);
        Assert.Equal(1, input.ActiveHandleCount);
        Assert.Equal(StateManagerStatus.Faulted, manager.Status);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        await manager.ResetAsync(CancellationToken.None);
        Assert.Equal(0, input.ActiveHandleCount);
    }

    [Fact]
    public async Task CancellationAfterCommitStartsIsDeferredUntilAllDomainsAndActivationFinish()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new Probe("first") { Committing = () => cancellation.Cancel() };
        var second = new Probe("second");
        var (manager, input, _) = await CreateAsync(first, second);
        await manager.RestoreGameAsync(Target(first, second), cancellation.Token);
        Assert.Equal(9, first.LiveValue);
        Assert.Equal(9, second.LiveValue);
        Assert.True(first.Activated && second.Activated);
        Assert.Equal(0, input.ActiveHandleCount);
        Assert.Equal(StateManagerStatus.Idle, manager.Status);
    }

    [Fact]
    public async Task PresentationTimeoutAndActivationFailureDoNotPublishSuccess()
    {
        foreach (var failActivation in new[] { false, true })
        {
            var probe = new Probe("game") { NeverPresented = !failActivation, FailActivation = failActivation };
            var (manager, input, _) = await CreateAsync(probe);
            manager.RestorePresentationTimeout = TimeSpan.FromMilliseconds(30);
            var completed = 0;
            manager.Restored += _ => completed++;
            await Assert.ThrowsAnyAsync<Exception>(() => manager.RestoreGameAsync(Target(probe)));
            Assert.Equal(StateManagerStatus.Faulted, manager.Status);
            Assert.Equal(1, input.ActiveHandleCount);
            Assert.Equal(0, completed);
            Assert.Null(manager.CurrentSnapshot);
            Assert.Equal(1, probe.Disposals);
            await manager.ResetAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PreparationReadsTargetDependenciesAndCommitHasNoInterleavedAwait()
    {
        var order = new List<string>();
        var first = new Probe("first") { Committing = () => order.Add("first") };
        var second = new Probe("second")
        {
            Dependency = "first",
            Committing = () =>
            {
                Assert.Equal(9, first.LiveValue);
                order.Add("second");
            },
        };
        var (manager, _, _) = await CreateAsync(first, second);
        await manager.RestoreGameAsync(Target(first, second));
        Assert.Equal(9, second.PreparedDependency);
        Assert.Equal(["first", "second"], order);
        Assert.True(first.Activated && second.Activated);
    }

    private static StateSnapshot Target(params Probe[] contributors) => new(
        StateSnapshot.CurrentFormatVersion, DateTimeOffset.UtcNow,
        contributors.Select(contributor => new StateRecord(contributor.Key, StateScope.Game, 1,
            JsonSerializer.SerializeToElement(9))));

    private static async Task<(StateManager, InputManager, Boundary)> CreateAsync(params Probe[] contributors)
    {
        var input = new InputManager(new FakeInputBackend());
        await input.InitializeAsync(CancellationToken.None);
        var boundary = new Boundary();
        var manager = new StateManager(contributors, input, boundary);
        await manager.InitializeAsync(CancellationToken.None);
        return (manager, input, boundary);
    }

    private sealed class Boundary : IRuntimeRestoreBoundary, IRuntimeRestoreOutput
    {
        public OutputLease Output { get; } = new();
        public bool Retained { get; private set; }
        public IRetainedRestoreOutput RetainRestoreOutput()
        {
            Retained = true;
            return Output;
        }
        public int Stops { get; private set; }
        public Task CancelForRestoreAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Retained);
            Stops++;
            return Task.CompletedTask;
        }
        public Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OutputLease : IRetainedRestoreOutput
    {
        public Action? OnPresent { get; set; }
        public int Presentations { get; private set; }
        public int Disposals { get; private set; }
        public Task WaitForTargetAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Presentations++;
            OnPresent?.Invoke();
            return Task.CompletedTask;
        }
        public void Dispose() => Disposals++;
    }

    private sealed class Probe(string key) : IPreparedStateContributor, IStateMutationParticipant
    {
        public void BindStateMutationGate(StateMutationGate gate) { }
        public string Key => key;
        public StateScope Scope => StateScope.Game;
        public int SchemaVersion => 1;
        public StateContributorCapability Capability => StateContributorCapability.DurableRollback;
        public string? Dependency { get; init; }
        public IReadOnlyCollection<string> Dependencies => Dependency is null ? [] : [Dependency];
        public int PreparedDependency { get; private set; }
        public int LiveValue { get; private set; } = 1;
        public int Disposals { get; private set; }
        public bool Activated { get; private set; }
        public bool FailPrepare { get; init; }
        public bool FailCommit { get; init; }
        public bool FailActivation { get; init; }
        public bool NeverPresented { get; init; }
        public Action? Preparing { get; init; }
        public Action? Committing { get; init; }
        public ValueTask<StateRecord> CaptureAsync(CancellationToken token) => ValueTask.FromResult(
            new StateRecord(Key, Scope, 1, JsonSerializer.SerializeToElement(LiveValue)));
        public ValueTask PreflightAsync(StateRecord record, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ResetAsync(CancellationToken token) =>
            throw new InvalidOperationException("Reset must never be called for replacement.");
        public ValueTask<IPreparedStateRestore> PrepareRestoreAsync(StateRecord? record,
            RestoreContext context, CancellationToken token)
        {
            if (FailPrepare) throw new InvalidOperationException("prepare failed");
            Preparing?.Invoke();
            token.ThrowIfCancellationRequested();
            if (Dependency is not null) PreparedDependency = context.Require(Dependency).Payload.GetInt32();
            return ValueTask.FromResult<IPreparedStateRestore>(new Candidate(this, record!.Payload.GetInt32()));
        }
        private sealed class Candidate(Probe owner, int target)
            : IPreparedStateRestore, IStateRestorePresentation, IStateRestoreActivation
        {
            public void Commit()
            {
                if (owner.FailCommit) throw new InvalidOperationException("commit failed");
                owner.Committing?.Invoke();
                owner.LiveValue = target;
            }
            public Task WaitForPresentationAsync(CancellationToken token) =>
                owner.NeverPresented ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
            public Task ActivateAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (owner.FailActivation) throw new InvalidOperationException("activation failed");
                owner.Activated = true;
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync()
            {
                owner.Disposals++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
