using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class StateMutationGateTests
{
    [Fact]
    public async Task FreezeRejectsConcurrentWritesAcrossAwaitAndReleasesExactlyOnce()
    {
        var gate = new StateMutationGate();
        var value = 1;
        var frozen = gate.Freeze();
        await Task.Run(() => Assert.Throws<StateRuntimeException>(() => gate.Write(() => value = 2)));
        Assert.Equal(1, value);
        frozen.Dispose();
        frozen.Dispose();
        gate.Write(() => value = 3);
        Assert.Equal(3, value);
    }

    [Fact]
    public void CaptureCannotStartInsideAnIncompleteWriteAndFailedWritesReleaseOwnership()
    {
        var gate = new StateMutationGate();
        Assert.Throws<StateRuntimeException>(() => gate.Write(() => gate.Freeze()));
        Assert.Throws<InvalidOperationException>(() => gate.Write(() => throw new InvalidOperationException()));
        using var frozen = gate.Freeze();
        Assert.Throws<StateRuntimeException>(() => gate.Write(() => { }));
    }

    [Fact]
    public void NestedWritersShareOneSynchronousCommitAndNestedReadersKeepStateFrozen()
    {
        var gate = new StateMutationGate();
        gate.Write(() => gate.Write(() => { }));
        using var outer = gate.Freeze();
        using (gate.Freeze()) { }
        Assert.Throws<StateRuntimeException>(() => gate.Write(() => { }));
    }

    [Fact]
    public async Task FreezeWaitsForAsyncCommitAndQueuedWriterRunsAfterSnapshot()
    {
        var gate = new StateMutationGate();
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = 0;
        var write = gate.WriteAsync(async _ =>
        {
            commitEntered.SetResult();
            await releaseCommit.Task;
            value = 1;
        });
        await commitEntered.Task;

        var freeze = gate.FreezeAsync().AsTask();
        Assert.False(freeze.IsCompleted);
        var queuedWrite = gate.WriteAsync(_ =>
        {
            value = 2;
            return Task.CompletedTask;
        });
        Assert.False(queuedWrite.IsCompleted);

        releaseCommit.SetResult();
        await write;
        using (await freeze)
        {
            Assert.Equal(1, value);
            Assert.False(queuedWrite.IsCompleted);
        }

        await queuedWrite;
        Assert.Equal(2, value);
    }

    [Fact]
    public async Task QueuedAsyncWriterObservesCancellationWithoutWaitingForFreezeRelease()
    {
        var gate = new StateMutationGate();
        using var frozen = await gate.FreezeAsync();
        using var cancellation = new CancellationTokenSource();
        var write = gate.WriteAsync(_ => Task.CompletedTask, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => write);
    }
}
