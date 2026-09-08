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
            Assert.Same(entries[^1].Snapshot, await manager.CaptureGameAsync());
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

    private sealed class DelayedContributor : IStateContributor
    {
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
        public ValueTask PreflightAsync(StateRecord record, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask RestoreAsync(StateRecord record, CancellationToken token) => ValueTask.CompletedTask;
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
