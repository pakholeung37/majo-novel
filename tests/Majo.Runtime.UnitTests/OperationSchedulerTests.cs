using Majo.Runtime.Core;
using Majo.Runtime.Operations;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class OperationSchedulerTests
{
    [Fact(Timeout = 5_000)]
    public async Task StartReturnsWithoutWaitingForOperationCompletion()
    {
        var harness = await OperationHarness.CreateAsync();
        var handle = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "non-waiting"));

        Assert.Equal(OperationState.Running, handle.State);
        Assert.False(handle.Completion.IsCompleted);

        harness.Clock.AdvanceFrame();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(OperationState.Completed, handle.State);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ExplicitWaitRemainsPendingUntilOperationCompletes()
    {
        var harness = await OperationHarness.CreateAsync();
        var handle = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "waited"));

        var wait = harness.Scheduler.WaitAsync(OperationTarget.ForId("waited"));
        Assert.False(wait.IsCompleted);

        harness.Clock.AdvanceFrame();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(OperationState.Completed, handle.State);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task GroupWaitJoinsConcurrentOperations()
    {
        var harness = await OperationHarness.CreateAsync();
        var firstGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = harness.Scheduler.Start(new OperationDefinition(
            _ => firstGate.Task,
            id: "first",
            groupId: "setup"));
        var second = harness.Scheduler.Start(new OperationDefinition(
            _ => secondGate.Task,
            id: "second",
            groupId: "setup"));

        var joined = harness.Scheduler.WaitAsync(OperationTarget.ForGroup("setup"));
        firstGate.SetResult();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(joined.IsCompleted);
        Assert.False(second.Completion.IsCompleted);

        secondGate.SetResult();
        await joined.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(OperationState.Completed, first.State);
        Assert.Equal(OperationState.Completed, second.State);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CancelAndCompleteProduceDifferentFinalStates()
    {
        var harness = await OperationHarness.CreateAsync();
        var cancelledValue = 25;
        var cancelCleanupRan = false;
        var cancelled = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "cancelled",
            completeAsync: _ =>
            {
                cancelledValue = 100;
                return Task.CompletedTask;
            },
            cancelAsync: _ =>
            {
                cancelCleanupRan = true;
                return Task.CompletedTask;
            }));

        await harness.Scheduler.CancelAsync(OperationTarget.ForId("cancelled"));

        Assert.Equal(OperationState.Cancelled, cancelled.State);
        Assert.Equal(25, cancelledValue);
        Assert.True(cancelCleanupRan);

        var completedValue = 25;
        var completed = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "completed",
            completeAsync: _ =>
            {
                completedValue = 100;
                return Task.CompletedTask;
            }));

        await harness.Scheduler.CompleteAsync(OperationTarget.ForId("completed"));

        Assert.Equal(OperationState.Completed, completed.State);
        Assert.Equal(100, completedValue);
        Assert.True(completed.Completion.IsCompletedSuccessfully);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task FaultPropagatesAndCompletedEntryRemainsAwaitableForTheSession()
    {
        var logger = new RecordingLogger();
        var harness = await OperationHarness.CreateAsync(logger);
        var handle = harness.Scheduler.Start(new OperationDefinition(
            _ => Task.FromException(new ExpectedException()),
            id: "faulted"));

        await Assert.ThrowsAsync<ExpectedException>(() => handle.Completion);
        await WaitUntilAsync(
            () => harness.Scheduler.ActiveOperations.Count == 0 &&
                logger.Exceptions.Count == 1,
            TimeSpan.FromSeconds(1));

        Assert.Equal(OperationState.Faulted, handle.State);
        Assert.Single(logger.Exceptions);
        await Assert.ThrowsAsync<ExpectedException>(() =>
            harness.Scheduler.WaitAsync(OperationTarget.ForId("faulted")));
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CompletedIdAndGroupRemainAwaitableUntilReset()
    {
        var harness = await OperationHarness.CreateAsync();
        var first = harness.Scheduler.Start(new OperationDefinition(
            _ => Task.CompletedTask,
            id: "instant",
            groupId: "instant-group"));

        await first.Completion;
        Assert.Empty(harness.Scheduler.ActiveOperations);

        await harness.Scheduler.WaitAsync(OperationTarget.ForId("instant"));
        await harness.Scheduler.WaitAsync(OperationTarget.ForGroup("instant-group"));

        await harness.Host.ResetAsync();
        await Assert.ThrowsAsync<OperationNotFoundException>(() =>
            harness.Scheduler.WaitAsync(OperationTarget.ForId("instant")));
        await Assert.ThrowsAsync<OperationNotFoundException>(() =>
            harness.Scheduler.WaitAsync(OperationTarget.ForGroup("instant-group")));
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task GroupWaitIncludesMembersThatFinishedBeforeAwaitStarted()
    {
        var harness = await OperationHarness.CreateAsync();
        var completed = harness.Scheduler.Start(new OperationDefinition(
            _ => Task.CompletedTask,
            groupId: "mixed"));
        await completed.Completion;
        var pendingGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Scheduler.Start(new OperationDefinition(
            _ => pendingGate.Task,
            groupId: "mixed"));

        var wait = harness.Scheduler.WaitAsync(OperationTarget.ForGroup("mixed"));
        Assert.False(wait.IsCompleted);
        pendingGate.SetResult();
        await wait;
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ActiveIdCannotBeReusedButCompletedIdCan()
    {
        var harness = await OperationHarness.CreateAsync();
        var firstGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = harness.Scheduler.Start(new OperationDefinition(
            _ => firstGate.Task,
            id: "unique"));

        Assert.Throws<DuplicateOperationIdException>(() =>
            harness.Scheduler.Start(new OperationDefinition(
                _ => Task.CompletedTask,
                id: "unique")));

        firstGate.SetResult();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(harness.Scheduler.ActiveOperations);

        var reused = harness.Scheduler.Start(new OperationDefinition(
            _ => Task.CompletedTask,
            id: "unique"));
        await reused.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(OperationState.Completed, reused.State);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ResetCancelsEveryOperationAndAllowsANewSession()
    {
        var harness = await OperationHarness.CreateAsync();
        var first = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "first-reset",
            groupId: "reset"));
        var second = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "second-reset",
            groupId: "reset"));
        var previousSession = harness.Host.SessionCancellationToken;

        await harness.Host.ResetAsync();
        await WaitUntilAsync(
            () => harness.Scheduler.ActiveOperations.Count == 0,
            TimeSpan.FromSeconds(1));

        Assert.True(previousSession.IsCancellationRequested);
        Assert.Equal(OperationState.Cancelled, first.State);
        Assert.Equal(OperationState.Cancelled, second.State);
        Assert.Empty(harness.Scheduler.ActiveOperations);

        var nextSession = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "new-session"));
        harness.Clock.AdvanceFrame();
        await nextSession.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(OperationState.Completed, nextSession.State);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task GroupCompleteForcesEveryMemberToItsFinalValue()
    {
        var harness = await OperationHarness.CreateAsync();
        var values = new[] { 10, 20 };
        var first = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "complete-first",
            groupId: "complete-group",
            completeAsync: _ =>
            {
                values[0] = 100;
                return Task.CompletedTask;
            }));
        var second = harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "complete-second",
            groupId: "complete-group",
            completeAsync: _ =>
            {
                values[1] = 200;
                return Task.CompletedTask;
            }));

        await harness.Scheduler.CompleteAsync(OperationTarget.ForGroup("complete-group"));

        Assert.Equal([100, 200], values);
        Assert.Equal(OperationState.Completed, first.State);
        Assert.Equal(OperationState.Completed, second.State);
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task TimedOutShutdownCanRetryUntilOperationCleanupFinishes()
    {
        var harness = await OperationHarness.CreateAsync();
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Scheduler.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            id: "slow-cleanup",
            cancelAsync: async _ =>
            {
                cleanupStarted.SetResult();
                await cleanupGate.Task;
            }));
        using var cancellation = new CancellationTokenSource();

        var firstShutdown = harness.Host.ShutdownAsync(cancellation.Token);
        await cleanupStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<RuntimeLifecycleException>(() => firstShutdown);
        Assert.Equal(RuntimeHostState.Faulted, harness.Host.State);

        cleanupGate.SetResult();
        await harness.Host.ShutdownAsync();
        Assert.Equal(RuntimeHostState.Shutdown, harness.Host.State);
        Assert.Empty(harness.Scheduler.ActiveOperations);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started >= timeout)
            {
                throw new TimeoutException("Condition was not reached before the test timeout.");
            }

            await Task.Yield();
        }
    }

    private sealed class OperationHarness
    {
        private OperationHarness(
            RuntimeHost host,
            ManualFrameClock clock,
            IOperationScheduler scheduler)
        {
            Host = host;
            Clock = clock;
            Scheduler = scheduler;
        }

        public RuntimeHost Host { get; }

        public ManualFrameClock Clock { get; }

        public IOperationScheduler Scheduler { get; }

        public static async Task<OperationHarness> CreateAsync(IRuntimeLogger? logger = null)
        {
            var clock = new ManualFrameClock();
            var registry = new RuntimeServiceRegistry()
                .AddSingleton<IFrameClock>(_ => clock)
                .AddSingleton<IOperationScheduler>(
                    resolver => new OperationScheduler(
                        resolver.Get<IFrameClock>(),
                        resolver.Get<IRuntimeLifetime>(),
                        logger),
                    typeof(IFrameClock),
                    typeof(IRuntimeLifetime));
            var host = registry.Build(logger);
            await host.InitializeAsync();
            return new OperationHarness(
                host,
                clock,
                host.Services.Get<IOperationScheduler>());
        }
    }

    private sealed class ManualFrameClock : IFrameClock
    {
        private readonly object gate = new();
        private readonly List<TaskCompletionSource> frameWaiters = new();
        private bool isInitialized;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            isInitialized = true;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            isInitialized = false;
            TaskCompletionSource[] waiters;
            lock (gate)
            {
                waiters = frameWaiters.ToArray();
                frameWaiters.Clear();
            }

            foreach (var waiter in waiters)
            {
                waiter.TrySetCanceled(cancellationToken);
            }

            return Task.CompletedTask;
        }

        public async Task NextFrameAsync(CancellationToken cancellationToken = default)
        {
            if (!isInitialized)
            {
                throw new InvalidOperationException("Manual clock is not initialized.");
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                frameWaiters.Add(completion);
            }

            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            await completion.Task;
        }

        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            return duration == TimeSpan.Zero
                ? Task.CompletedTask
                : NextFrameAsync(cancellationToken);
        }

        public void AdvanceFrame()
        {
            TaskCompletionSource[] waiters;
            lock (gate)
            {
                waiters = frameWaiters.ToArray();
                frameWaiters.Clear();
            }

            foreach (var waiter in waiters)
            {
                waiter.TrySetResult();
            }
        }
    }

    private sealed class RecordingLogger : IRuntimeLogger
    {
        public List<Exception> Exceptions { get; } = new();

        public bool IsEnabled(string category, RuntimeLogLevel level) => true;

        public void Log(RuntimeLogEvent logEvent)
        {
            if (logEvent.Exception is not null)
            {
                Exceptions.Add(logEvent.Exception);
            }
        }
    }

    private sealed class ExpectedException : Exception
    {
    }
}
