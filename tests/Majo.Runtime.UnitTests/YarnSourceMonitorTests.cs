using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class YarnSourceMonitorTests
{
    [Fact]
    public async Task InitialScanEstablishesBaselineAndOnlyChangedContentRequestsReload()
    {
        using var directory = new MonitorDirectory();
        var value = "first";
        var baselines = new List<string>();
        var changes = 0;
        await using var monitor = directory.Create(_ => Task.FromResult(value), baselines.Add, () => changes++);

        await monitor.ScanAsync();
        await monitor.ScanAsync();
        value = "second";
        await monitor.ScanAsync();
        await monitor.ScanAsync();

        Assert.Equal(new[] { "first" }, baselines);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ConcurrentScansShareTheSameRead()
    {
        using var directory = new MonitorDirectory();
        var read = NewCompletion<string>();
        var reads = 0;
        await using var monitor = directory.Create(_ => { reads++; return read.Task; });
        var first = monitor.ScanAsync();
        var second = monitor.ScanAsync();
        Assert.Same(first, second);
        Assert.Equal(1, reads);
        read.SetResult("first");
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompiledFingerprintSupersedesAnOlderInFlightScan()
    {
        using var directory = new MonitorDirectory();
        var read = NewCompletion<string>();
        var baselines = new List<string>();
        var changes = 0;
        await using var monitor = directory.Create(_ => read.Task, baselines.Add, () => changes++);
        var scan = monitor.ScanAsync();
        monitor.AcceptFingerprint("compiled");
        read.SetResult("old");
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        read = NewCompletion<string>();
        read.SetResult("compiled");
        await monitor.ScanAsync();

        Assert.Empty(baselines);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ResetDiscardsOldScanAndReestablishesBaseline()
    {
        using var directory = new MonitorDirectory();
        var read = NewCompletion<string>();
        var baselines = new List<string>();
        var changes = 0;
        await using var monitor = directory.Create(_ => read.Task, baselines.Add, () => changes++);
        monitor.AcceptFingerprint("previous");
        var scan = monitor.ScanAsync();
        monitor.ResetBaseline();
        read.SetResult("stale");
        await scan.WaitAsync(TimeSpan.FromSeconds(5));
        read = NewCompletion<string>();
        read.SetResult("new-session");
        await monitor.ScanAsync();

        Assert.Equal(new[] { "new-session" }, baselines);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task DisposalDrainsReaderAndSuppressesItsLateNotification()
    {
        using var directory = new MonitorDirectory();
        var read = NewCompletion<string>();
        var notifications = 0;
        CancellationToken token = default;
        var monitor = directory.Create(current => { token = current; return read.Task; },
            _ => notifications++, () => notifications++);
        var scan = monitor.ScanAsync();
        var stop = monitor.DisposeAsync().AsTask();
        Assert.True(token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        Assert.Same(stop, monitor.DisposeAsync().AsTask());
        read.SetResult("late");
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await scan;
        await monitor.ScanAsync();
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task DisposalDrainsReaderEvenIfCancellationCallbackFails()
    {
        using var directory = new MonitorDirectory();
        var read = NewCompletion<string>();
        CancellationTokenRegistration registration = default;
        var monitor = directory.Create(token =>
        {
            registration = token.Register(() => throw new InvalidOperationException("cancel failed"));
            return read.Task;
        });
        _ = monitor.ScanAsync();
        var stop = monitor.DisposeAsync().AsTask();
        Assert.False(stop.IsCompleted);
        read.SetResult("late");
        var error = await Assert.ThrowsAsync<AggregateException>(() => stop.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(error.Flatten().InnerExceptions, exception => exception.Message == "cancel failed");
        registration.Dispose();
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class MonitorDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "majo-monitor-tests", Guid.NewGuid().ToString("N"));
        public MonitorDirectory() => Directory.CreateDirectory(root);
        public YarnSourceMonitor Create(Func<CancellationToken, Task<string>> read,
            Action<string>? baseline = null, Action? changed = null) =>
            new(root, null, read, baseline ?? (_ => { }), changed ?? (() => { }), NullRuntimeLogger.Instance);
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
