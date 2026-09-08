using Majo.Runtime.Script;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class LineBoundaryGateTests
{
    [Fact]
    public async Task RepeatedAdvanceDoesNotCancelDelayedPreparation()
    {
        using var gate = new LineBoundaryGate();
        gate.BeginSession();
        using var line = gate.Enter();
        var capture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = capture.Task.WaitAsync(line.Token);
        for (var index = 0; index < 100; index++) Assert.True(gate.DeferAdvance());
        Assert.False(waiting.IsCompleted);
        Assert.False(line.Token.IsCancellationRequested);
        capture.SetResult();
        await waiting;
        var advances = 0;
        line.Activate(() => advances++, () => { });
        Assert.Equal(1, advances);
        Assert.False(gate.IsPreparing);
        Assert.False(gate.DeferAdvance());
    }

    [Fact]
    public async Task StopCancelsCaptureAndDiscardsPendingAdvance()
    {
        using var gate = new LineBoundaryGate();
        gate.BeginSession();
        using var line = gate.Enter();
        Assert.True(gate.DeferAdvance());
        var waiting = Task.Delay(Timeout.Infinite, line.Token);
        gate.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Throws<OperationCanceledException>(() => line.Activate(
            () => Assert.Fail("Cancelled boundary advanced."), () => { }));
        line.Dispose();
        Assert.False(gate.IsPreparing);
    }

    [Fact]
    public void FailedPreparationDiscardsInputWithoutPublishing()
    {
        using var gate = new LineBoundaryGate();
        gate.BeginSession();
        using (var failed = gate.Enter()) Assert.True(gate.DeferAdvance());
        using var retry = gate.Enter();
        retry.Activate(() => Assert.Fail("Input leaked from failed preparation."), () => { });
    }

    [Fact]
    public void OldSessionCannotReleaseNewBoundary()
    {
        using var gate = new LineBoundaryGate();
        gate.BeginSession();
        using var old = gate.Enter();
        gate.BeginSession();
        using var current = gate.Enter();
        Assert.True(current.Generation > old.Generation);
        old.Dispose();
        Assert.True(gate.IsPreparing);
        Assert.True(gate.DeferAdvance());
        var advanced = false;
        current.Activate(() => advanced = true, () => { });
        Assert.True(advanced);
    }

    [Fact]
    public void EveryOccurrenceGetsAnIdentityEvenWhenPresentationCompletesImmediately()
    {
        using var gate = new LineBoundaryGate();
        gate.BeginSession();
        for (var index = 1; index <= 100; index++)
        {
            using var boundary = gate.Enter();
            Assert.Equal(index, boundary.Sequence);
            boundary.Activate(() => { }, () => { });
        }
    }

    [Fact]
    public void OverlappingPreparationIsRejected()
    {
        using var gate = new LineBoundaryGate();
        using var line = gate.Enter();
        Assert.Throws<InvalidOperationException>(() => gate.Enter());
    }
}
