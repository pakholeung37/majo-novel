using System.Text.Json;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Input;
using Majo.Runtime.Core;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class LineBoundaryStateTests
{
    [Fact]
    public async Task FrozenSaveAllowsVisualActivitiesButStillHonorsExternalBarriers()
    {
        var source = new BoundarySource();
        var script = new FakeScriptPlayback();
        var input = new InputManager(new FakeInputBackend());
        var activities = new RuntimeActivityRegistry();
        var eligibility = new StateEligibility(script, activities, input);
        await eligibility.InitializeAsync(CancellationToken.None);
        using var manager = new StateManager([], input, new RestoreBoundary(), eligibility, boundarySource: source);
        await manager.InitializeAsync(CancellationToken.None);
        Assert.False(eligibility.Check(StateOperationKind.Save).IsAllowed);
        script.SetPhase(ScriptPlaybackPhase.PreparingLineBoundary);
        Assert.True(eligibility.Check(StateOperationKind.Save).IsAllowed);
        await source.CommitAsync(Boundary(1));
        script.SetPhase(ScriptPlaybackPhase.PresentingLine);
        using var animation = activities.BeginActivity(RuntimeActivityOwner.Direction, "long-animation");
        Assert.True(eligibility.Check(StateOperationKind.Save).IsAllowed);
        Assert.Same(manager.CurrentBoundary, await manager.CaptureSaveBoundaryAsync());
        using var barrier = eligibility.AcquireBarrier(this, "explicit-save-barrier", StateOperationMask.Save);
        Assert.Equal(StateEligibilityReasonCode.ExternalBarrier,
            Assert.Single(eligibility.Check(StateOperationKind.Save).Reasons).Code);
        Assert.True(activities.HasActiveActivities());
    }

    [Fact]
    public async Task CapturesEveryOccurrenceDespiteSaveEligibilityAndFreezesScriptIdentity()
    {
        var source = new BoundarySource();
        var script = new FakeScriptPlayback();
        using var manager = new StateManager([new ScriptStateContributor(script)],
            new InputManager(new FakeInputBackend()), new RestoreBoundary(), new DenySaveEligibility(), boundarySource: source);
        await manager.InitializeAsync(CancellationToken.None);
        var entries = new List<CommittedLineBoundary>();
        manager.BoundaryCommitted += entries.Add;
        for (var sequence = 1; sequence <= 100; sequence++)
        {
            var boundary = Boundary(sequence);
            await source.CommitAsync(boundary);
        }
        Assert.Equal(100, entries.Count);
        Assert.Equal(Enumerable.Range(1, 100).Select(value => (long)value),
            entries.Select(entry => entry.Context.OccurrenceSequence));
        Assert.All(entries, entry => Assert.Equal(entry.Context.OccurrenceSequence.ToString(),
            entry.Snapshot.Records[BuiltInStateKeys.ScriptPlayback].Payload
                .GetProperty("resolvedSubstitutions")[0].GetString()));
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SaveWaitsForDelayedCaptureAndCancellationPublishesNothing()
    {
        var source = new BoundarySource();
        var delayed = new DelayedContributor();
        using var manager = new StateManager([delayed], new InputManager(new FakeInputBackend()),
            new RestoreBoundary(), boundarySource: source);
        await manager.InitializeAsync(CancellationToken.None);
        var publications = 0;
        manager.BoundaryCommitted += _ => publications++;
        using var cancellation = new CancellationTokenSource();
        var capture = source.CommitAsync(Boundary(1), cancellation.Token);
        await delayed.Entered.Task;
        var save = manager.CaptureGameAsync();
        Assert.False(save.IsCompleted);
        Assert.Null(manager.CurrentSnapshot);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        await Assert.ThrowsAsync<StateEligibilityException>(() => save);
        Assert.Equal(0, publications);
        Assert.Null(manager.CurrentSnapshot);
        // No committed boundary is an expected save rejection, not a broken capture.
        await manager.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StateKeepsParticipantsFrozenThroughoutDelayedCaptureAndUnfreezesAfterCancellation()
    {
        var source = new BoundarySource();
        var delayed = new DelayedContributor();
        using var manager = new StateManager([delayed], new InputManager(new FakeInputBackend()),
            new RestoreBoundary(), boundarySource: source);
        await manager.InitializeAsync(CancellationToken.None);
        delayed.Write();
        using var cancellation = new CancellationTokenSource();
        var capture = source.CommitAsync(Boundary(1), cancellation.Token);
        await delayed.Entered.Task;
        Assert.Throws<StateRuntimeException>(delayed.Write);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        delayed.Write();
    }

    [Fact]
    public async Task StaleBoundaryCredentialIsRejected()
    {
        var source = new BoundarySource();
        using var manager = new StateManager([], new InputManager(new FakeInputBackend()), new RestoreBoundary(), boundarySource: source);
        await manager.InitializeAsync(CancellationToken.None);
        var boundary = Boundary(1);
        await source.CommitAsync(boundary);
        await Assert.ThrowsAsync<StateSnapshotException>(() =>
            source.CommitLineBoundaryAsync!(boundary, CancellationToken.None));
        Assert.Same(boundary, manager.CurrentBoundary!.Context);
    }

    [Fact]
    public async Task SaveRefreshesTheCurrentOccurrenceAfterPersistentUiMutation()
    {
        var source = new BoundarySource();
        var ui = new MutableContributor();
        using var manager = new StateManager([ui], new InputManager(new FakeInputBackend()),
            new RestoreBoundary(), boundarySource: source);
        await manager.InitializeAsync(CancellationToken.None);
        var publications = new List<CommittedLineBoundary>();
        manager.BoundaryCommitted += publications.Add;

        await source.CommitAsync(Boundary(1));
        var first = manager.CurrentBoundary!;
        ui.Set(2);
        var revised = await manager.CaptureSaveBoundaryAsync();

        Assert.Equal(2, publications.Count);
        Assert.Same(first.Context, revised.Context);
        Assert.NotSame(first.Snapshot, revised.Snapshot);
        Assert.True(revised.MutationRevision > first.MutationRevision);
        Assert.Equal(2, revised.Snapshot.Records[ui.Key].Payload.GetInt32());
        Assert.Same(revised, await manager.CaptureSaveBoundaryAsync());
        Assert.Equal(2, publications.Count);
    }

    [Fact]
    public async Task SaveDuringPreludeKeepsFrozenStateUntilNextBoundary()
    {
        var source = new BoundarySource();
        var state = new MutableContributor();
        using var manager = new StateManager([state], new InputManager(new FakeInputBackend()),
            new RestoreBoundary(), boundarySource: source);
        await manager.InitializeAsync(CancellationToken.None);
        await source.CommitAsync(Boundary(1));
        var committed = manager.CurrentBoundary!;
        source.CanRevise = false;
        state.Set(2); // A command after the previous line, before the next line entry.
        Assert.Same(committed, await manager.CaptureSaveBoundaryAsync());
        Assert.Equal(1, committed.Snapshot.Records[state.Key].Payload.GetInt32());
        await source.CommitAsync(Boundary(2));
        Assert.Equal(2, (await manager.CaptureSaveBoundaryAsync()).Snapshot.Records[state.Key].Payload.GetInt32());
    }

    [Fact]
    public async Task SaveRechecksOccurrenceAfterWaitingForPersistentWriter()
    {
        var source = new BoundarySource();
        var state = new MutableContributor();
        using var manager = new StateManager([state], new InputManager(new FakeInputBackend()),
            new RestoreBoundary(), boundarySource: source);
        await manager.InitializeAsync(CancellationToken.None);
        await source.CommitAsync(Boundary(1));
        var committed = manager.CurrentBoundary!;
        state.Set(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = state.WriteAsync(async () => await release.Task);
        var saving = manager.CaptureSaveBoundaryAsync();
        Assert.False(saving.IsCompleted);
        source.CanRevise = false;
        release.SetResult();
        await writing;
        Assert.Same(committed, await saving);
    }

    private static LineBoundaryContext Boundary(long sequence) => new(1, sequence,
        new ScriptCheckpoint(ScriptCheckpoint.CurrentFormatVersion, "fingerprint",
            new ScriptPlaybackSpot(new ScriptId("main", "Start"), "line:loop"), [sequence.ToString()]),
        new DialogueLine("line:loop", null, new DialogueText(sequence.ToString(), []), []));

    private sealed class BoundarySource : ILineBoundarySource
    {
        private LineBoundaryContext? active;
        private TaskCompletionSource? preparing;
        public event Action<bool>? PreparationChanged { add { } remove { } }
        public Func<LineBoundaryContext, CancellationToken, Task>? CommitLineBoundaryAsync { get; set; }
        public Task<IDisposable> AcquireRestoreAsync(CancellationToken token) => throw new NotSupportedException();
        public Func<ScriptCheckpoint, DialoguePresentationOrigin, CancellationToken, Task>? ResumeCheckpointAsync { get; set; }
        public Task ResumeBoundaryAsync(ScriptCheckpoint checkpoint, DialoguePresentationOrigin origin,
            CancellationToken token) => throw new NotSupportedException();
        public bool CanRevise { get; set; } = true;
        public bool CanReviseBoundary(LineBoundaryContext boundary) => CanRevise;
        public bool IsCurrentBoundary(LineBoundaryContext boundary) => ReferenceEquals(active, boundary);
        public Task WaitForPreparationAsync(CancellationToken token) =>
            preparing?.Task.WaitAsync(token) ?? Task.CompletedTask;
        public async Task CommitAsync(LineBoundaryContext boundary, CancellationToken token = default)
        {
            active = boundary;
            preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try { await CommitLineBoundaryAsync!(boundary, token); }
            finally
            {
                active = null;
                preparing.TrySetResult();
                preparing = null;
            }
        }
    }

    private sealed class DelayedContributor : IPreparedStateContributor, IStateMutationParticipant
    {
        private StateMutationGate? gate;
        public void BindStateMutationGate(StateMutationGate value) => gate = value;
        public void Write() => gate!.Write(() => { });
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Key => "delayed";
        public StateScope Scope => StateScope.Game;
        public int SchemaVersion => 1;
        public StateContributorCapability Capability => StateContributorCapability.DurableRollback;
        public async ValueTask<StateRecord> CaptureAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new StateRecord(Key, Scope, 1, JsonSerializer.SerializeToElement(1));
        }
        public ValueTask<IPreparedStateRestore> PrepareRestoreAsync(StateRecord? record,
            RestoreContext context, CancellationToken token) =>
            ValueTask.FromResult<IPreparedStateRestore>(new PreparedStateRestore(() => { }));
        public ValueTask PreflightAsync(StateRecord record, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ResetAsync(CancellationToken token) => ValueTask.CompletedTask;
    }

    private sealed class MutableContributor : IPreparedStateContributor, IStateMutationParticipant
    {
        private StateMutationGate? gate;
        private int value = 1;
        public string Key => "ui";
        public StateScope Scope => StateScope.Game;
        public int SchemaVersion => 1;
        public StateContributorCapability Capability => StateContributorCapability.DurableRollback;
        public void BindStateMutationGate(StateMutationGate gate) => this.gate = gate;
        public void Set(int next) => gate!.Write(() => value = next);
        public Task WriteAsync(Func<Task> write) => gate!.WriteAsync(_ => write());
        public ValueTask<StateRecord> CaptureAsync(CancellationToken token) => ValueTask.FromResult(
            new StateRecord(Key, Scope, SchemaVersion, JsonSerializer.SerializeToElement(value)));
        public ValueTask<IPreparedStateRestore> PrepareRestoreAsync(StateRecord? record,
            RestoreContext context, CancellationToken token) =>
            ValueTask.FromResult<IPreparedStateRestore>(new PreparedStateRestore(() => { }));
        public ValueTask PreflightAsync(StateRecord record, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ResetAsync(CancellationToken token) => ValueTask.CompletedTask;
    }

    private sealed class RestoreBoundary : IRuntimeRestoreBoundary
    {
        public Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelForRestoreAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DenySaveEligibility : IStateEligibility
    {
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken token) => Task.CompletedTask;
        public StateEligibilityResult Check(StateOperationKind operation) => new(operation,
            [new StateEligibilityReason(StateEligibilityReasonCode.ActiveOperation, "Long visual animation")]);
        public IStateBarrierHandle AcquireBarrier(object owner, string reasonCode,
            StateOperationMask operations = StateOperationMask.All, string? diagnostic = null,
            string? playerMessage = null) => throw new NotSupportedException();
    }
}
