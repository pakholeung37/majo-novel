#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Operations;
using Majo.Runtime.Timing;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class TransientCueTransactionTests
{
    private static readonly DirectionCue DummyCue = new("test-cue", new Dictionary<string, string>());
    private static readonly StageCreatePatch DummyVisual = new(
        new StageId("main"),
        new ViewId("main"),
        new DirectionTransitionSpec(DirectionTransitionId.Fade, 0.5, EasingId.Linear, Skippable: true));

    [Fact(Timeout = 5_000)]
    public async Task TransactionSettlement_WaitsForBothVisualTransitionAndCueActivity()
    {
        var backend = new FakeDirectionBackend { AutoSettle = false };
        var cueExecutor = new RecordingCueExecutor();
        FakeCueActivity? fakeActivity = null;
        cueExecutor.ActivityFactory = _ =>
        {
            fakeActivity = new FakeCueActivity();
            return fakeActivity;
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: cueExecutor);

        var transaction = await coordinator.ExecuteAsync([DummyVisual, DummyCue], DirectionCompletionBarrier.Commit);

        Assert.NotNull(fakeActivity);
        Assert.False(transaction.Settlement.IsCompleted);

        // Complete only visual settlement
        backend.LastSettlementTcs!.TrySetResult();
        await Task.Yield();
        Assert.False(transaction.Settlement.IsCompleted);

        // Complete cue settlement
        fakeActivity.CompleteSettlement();
        await transaction.Settlement;

        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
    }

    [Fact(Timeout = 5_000)]
    public async Task FinishSkippableAsync_FinishesBothVisualTransitionsAndCueActivity()
    {
        var backend = new FakeDirectionBackend { AutoSettle = false };
        var cueExecutor = new RecordingCueExecutor();
        FakeCueActivity? fakeActivity = null;
        cueExecutor.ActivityFactory = _ =>
        {
            fakeActivity = new FakeCueActivity();
            return fakeActivity;
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: cueExecutor);

        var transaction = await coordinator.ExecuteAsync([DummyVisual, DummyCue], DirectionCompletionBarrier.Commit);
        Assert.NotNull(fakeActivity);
        Assert.True(transaction.IsSkippable);
        Assert.False(fakeActivity.WasFinished);
        Assert.Equal(0, backend.FinishCount);

        await coordinator.FinishSkippableAsync();

        Assert.True(fakeActivity.WasFinished);
        Assert.Equal(1, backend.FinishCount);
        Assert.Equal(DirectionTransactionStatus.Settled, transaction.Status);
    }

    [Fact(Timeout = 5_000)]
    public async Task FinishSkippableAsync_DoesNotSkip_WhenCueIsNotSkippable()
    {
        var backend = new FakeDirectionBackend { AutoSettle = false };
        var cueExecutor = new RecordingCueExecutor();
        FakeCueActivity? fakeActivity = null;
        cueExecutor.ActivityFactory = _ =>
        {
            fakeActivity = new FakeCueActivity { IsSkippable = false };
            return fakeActivity;
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: cueExecutor);

        var transaction = await coordinator.ExecuteAsync([DummyVisual, DummyCue], DirectionCompletionBarrier.Commit);
        Assert.NotNull(fakeActivity);
        Assert.False(transaction.IsSkippable);

        await coordinator.FinishSkippableAsync();

        Assert.False(fakeActivity.WasFinished);
        Assert.Equal(0, backend.FinishCount);
        Assert.False(transaction.Settlement.IsCompleted);

        // Clean up uncompleted tasks to avoid lingering background tasks
        backend.LastSettlementTcs!.TrySetResult();
        fakeActivity.CompleteSettlement();
        await transaction.Settlement;
    }

    [Fact(Timeout = 5_000)]
    public async Task CueStartupFailure_CancelsStartedCues_RollsBackPreparedTransaction_AndRestoresState()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var cueExecutor = new RecordingCueExecutor
        {
            StartException = new InvalidOperationException("Cue startup failed"),
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: cueExecutor);

        var beforeRevision = coordinator.State.Revision;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync([DummyVisual, DummyCue]));

        Assert.Equal("Cue startup failed", ex.Message);
        Assert.Equal(1, backend.RollbackCount);
        Assert.Equal(1, backend.DisposeCount);
        Assert.Equal(beforeRevision, coordinator.State.Revision);
    }

    [Fact(Timeout = 5_000)]
    public async Task CueFinishFailure_AggregatesWithVisualFinishFailure()
    {
        var backend = new FakeDirectionBackend
        {
            AutoSettle = false,
            FinishException = new InvalidOperationException("Visual finish failed"),
        };

        var cueExecutor = new RecordingCueExecutor();
        FakeCueActivity? fakeActivity = null;
        cueExecutor.ActivityFactory = _ =>
        {
            fakeActivity = new FakeCueActivity
            {
                FinishException = new InvalidOperationException("Cue finish failed"),
            };
            return fakeActivity;
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: cueExecutor);

        var transaction = await coordinator.ExecuteAsync([DummyVisual, DummyCue], DirectionCompletionBarrier.Commit);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => coordinator.FinishAsync(transaction));

        Assert.Contains(aggregate.InnerExceptions, e => e.Message == "Visual finish failed");
        Assert.Contains(aggregate.InnerExceptions, e => e.Message == "Cue finish failed");

        // Clean up lingering tasks
        backend.LastSettlementTcs!.TrySetResult();
        fakeActivity!.CompleteSettlement();
        try { await transaction.Settlement; } catch { }
    }

    [Fact(Timeout = 5_000)]
    public async Task CueCleanupFailure_AggregatesWithVisualCleanupFailure_OnReset()
    {
        var backend = new FakeDirectionBackend
        {
            AutoSettle = true,
            DisposeException = new InvalidOperationException("Visual dispose failed"),
        };

        var cueExecutor = new RecordingCueExecutor();
        cueExecutor.ActivityFactory = _ => new FakeCueActivity
        {
            DisposeException = new InvalidOperationException("Cue dispose failed"),
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: cueExecutor);

        await coordinator.ExecuteAsync([DummyVisual, DummyCue], DirectionCompletionBarrier.Commit);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => coordinator.ResetAsync());

        var allInner = aggregate.Flatten().InnerExceptions;
        Assert.Contains(allInner, e => e.Message == "Visual dispose failed");
        Assert.Contains(allInner, e => e.Message == "Cue dispose failed");
    }

    [Fact(Timeout = 5_000)]
    public async Task SettlementFailure_AggregatesBothSettlementAndDisposeFailures()
    {
        var backend = new FakeDirectionBackend
        {
            AutoSettle = true,
            DisposeException = new InvalidOperationException("Visual dispose failed"),
        };

        var cueExecutor = new RecordingCueExecutor();
        FakeCueActivity? fakeActivity = null;
        cueExecutor.ActivityFactory = _ =>
        {
            fakeActivity = new FakeCueActivity();
            return fakeActivity;
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: cueExecutor);

        var transaction = await coordinator.ExecuteAsync([DummyVisual, DummyCue], DirectionCompletionBarrier.Commit);

        // Fail cue settlement
        fakeActivity!.FailSettlement(new InvalidOperationException("Cue settlement failed"));

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => transaction.Settlement);

        Assert.Contains("Direction settlement and cleanup both failed.", aggregate.Message);
        var allInner = aggregate.Flatten().InnerExceptions;
        Assert.Contains(allInner, e => e.Message == "Cue settlement failed");
        Assert.Contains(allInner, e => e.Message == "Visual dispose failed");
    }

    [Fact(Timeout = 5_000)]
    public async Task RollbackAsync_CancelsActiveCueActivity()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var cueExecutor = new RecordingCueExecutor();
        FakeCueActivity? fakeActivity = null;
        cueExecutor.ActivityFactory = _ =>
        {
            fakeActivity = new FakeCueActivity();
            return fakeActivity;
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: cueExecutor);

        var transaction = await coordinator.ExecuteAsync([DummyVisual, DummyCue], DirectionCompletionBarrier.Commit);
        Assert.NotNull(fakeActivity);
        Assert.False(fakeActivity.WasCancelled);

        await coordinator.RollbackAsync(transaction);

        Assert.True(fakeActivity.WasCancelled);
    }

    [Fact(Timeout = 5_000)]
    public async Task Coordinator_ThrowsWhenPlanContainsCuesButNoCueExecutorConfigured()
    {
        var backend = new FakeDirectionBackend { AutoSettle = true };
        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            cues: null);

        var ex = await Assert.ThrowsAsync<DirectionException>(
            () => coordinator.ExecuteAsync([DummyCue]));

        Assert.Contains("no cue executor is configured", ex.Message);
    }

    [Fact(Timeout = 5_000)]
    public async Task ResetAndShutdown_CleansUpCueActivityAndReleasesActivityLeasesWithoutLeaks()
    {
        var activities = new RuntimeActivityRegistry();
        var backend = new FakeDirectionBackend { AutoSettle = false };
        var cueExecutor = new RecordingCueExecutor();
        FakeCueActivity? fakeActivity = null;
        cueExecutor.ActivityFactory = _ =>
        {
            fakeActivity = new FakeCueActivity();
            return fakeActivity;
        };

        var coordinator = new DirectionTransactionCoordinator(
            new DirectionPlanner(),
            backend,
            activities: activities,
            cues: cueExecutor);

        await coordinator.ExecuteAsync([DummyVisual, DummyCue], DirectionCompletionBarrier.Commit);

        Assert.NotNull(fakeActivity);
        Assert.True(activities.HasActiveActivities());

        await coordinator.ResetAsync();

        Assert.True(fakeActivity.WasFinished);
        Assert.True(fakeActivity.WasDisposed);
        Assert.False(activities.HasActiveActivities());
    }

    [Fact(Timeout = 5_000)]
    public async Task TransientCueActivity_FinishAndCancelContracts()
    {
        var completed = false;
        var cancelled = false;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handle = new FakeOperationHandle(
            onComplete: () =>
            {
                completed = true;
                tcs.TrySetResult();
            },
            onCancel: () =>
            {
                cancelled = true;
                tcs.TrySetCanceled();
            },
            completion: tcs.Task);

        var activity = new TransientCueActivity([handle], isSkippable: true);

        Assert.True(activity.IsSkippable);
        Assert.False(activity.Settlement.IsCompleted);

        await activity.FinishAsync();

        Assert.True(completed);
        Assert.False(cancelled);
        Assert.True(activity.Settlement.IsCompletedSuccessfully);

        // Test cancel contract on a new instance
        var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelHandle = new FakeOperationHandle(
            onComplete: () => cancelTcs.TrySetResult(),
            onCancel: () => cancelTcs.TrySetCanceled(),
            completion: cancelTcs.Task);

        var cancelActivity = new TransientCueActivity([cancelHandle], isSkippable: true);
        await cancelActivity.CancelAsync();

        Assert.True(cancelActivity.Settlement.IsCanceled);
    }

    private sealed class FakeOperationHandle : IOperationHandle
    {
        private readonly Action onComplete;
        private readonly Action onCancel;

        public FakeOperationHandle(Action onComplete, Action onCancel, Task completion)
        {
            this.onComplete = onComplete;
            this.onCancel = onCancel;
            Completion = completion;
        }

        public OperationState State => OperationState.Running;
        public Task Completion { get; }

        public void Finish() => onComplete();
        public void Cancel() => onCancel();
    }
}
