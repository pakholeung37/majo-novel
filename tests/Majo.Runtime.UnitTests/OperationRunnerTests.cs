using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class OperationRunnerTests
{
    [Fact(Timeout = 5_000)]
    public async Task StartReturnsWithoutWaitingForOperationCompletion()
    {
        var harness = await OperationHarness.CreateAsync();
        var handle = harness.Runner.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken)));

        Assert.Equal(OperationState.Running, handle.State);
        Assert.False(handle.Completion.IsCompleted);

        harness.Clock.AdvanceFrame();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Empty(harness.Activities.Snapshot());
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task FinishForcesOperationSettlementAndRunsCleanup()
    {
        var harness = await OperationHarness.CreateAsync();
        var completedValue = 25;
        var handle = harness.Runner.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            onFinish: _ =>
            {
                completedValue = 100;
                return Task.CompletedTask;
            }));

        handle.Finish();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal(100, completedValue);
        Assert.True(handle.Completion.IsCompletedSuccessfully);
        Assert.Empty(harness.Activities.Snapshot());
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CancelStopsOperationAndRunsCancelCleanup()
    {
        var harness = await OperationHarness.CreateAsync();
        var cancelledValue = 25;
        var cancelCleanupRan = false;
        var handle = harness.Runner.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            onFinish: _ =>
            {
                cancelledValue = 100;
                return Task.CompletedTask;
            },
            onCancel: _ =>
            {
                cancelCleanupRan = true;
                return Task.CompletedTask;
            }));

        handle.Cancel();
        try { await handle.Completion.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (OperationCanceledException) { }

        Assert.Equal(OperationState.Cancelled, handle.State);
        Assert.Equal(25, cancelledValue);
        Assert.True(cancelCleanupRan);
        Assert.Empty(harness.Activities.Snapshot());
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task FinishAndCancelAreIdempotent()
    {
        var harness = await OperationHarness.CreateAsync();
        var finishCalls = 0;
        var cancelCalls = 0;
        var handle = harness.Runner.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            onFinish: _ =>
            {
                finishCalls++;
                return Task.CompletedTask;
            },
            onCancel: _ =>
            {
                cancelCalls++;
                return Task.CompletedTask;
            }));

        handle.Finish();
        handle.Finish();
        handle.Cancel();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(OperationState.Completed, handle.State);
        Assert.Equal(1, finishCalls);
        Assert.Equal(0, cancelCalls);
        Assert.Empty(harness.Activities.Snapshot());
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task FaultPropagatesToCompletionAndLogs()
    {
        var logger = new RecordingLogger();
        var harness = await OperationHarness.CreateAsync(logger);
        var handle = harness.Runner.Start(new OperationDefinition(
            _ => Task.FromException(new ExpectedException())));

        await Assert.ThrowsAsync<ExpectedException>(() => handle.Completion);
        await WaitUntilAsync(
            () => harness.Activities.Snapshot().Count == 0 &&
                logger.Exceptions.Count == 1,
            TimeSpan.FromSeconds(1));

        Assert.Equal(OperationState.Faulted, handle.State);
        Assert.Single(logger.Exceptions);
        Assert.Empty(harness.Activities.Snapshot());
        await harness.Host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ResetCancelsEveryActiveOperation()
    {
        var harness = await OperationHarness.CreateAsync();
        var first = harness.Runner.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken)));
        var second = harness.Runner.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken)));
        var previousSession = harness.Host.SessionCancellationToken;

        await harness.Host.ResetAsync();
        await WaitUntilAsync(
            () => harness.Activities.Snapshot().Count == 0,
            TimeSpan.FromSeconds(1));

        Assert.True(previousSession.IsCancellationRequested);
        Assert.Equal(OperationState.Cancelled, first.State);
        Assert.Equal(OperationState.Cancelled, second.State);
        Assert.Empty(harness.Activities.Snapshot());

        var nextSession = harness.Runner.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken)));
        harness.Clock.AdvanceFrame();
        await nextSession.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(OperationState.Completed, nextSession.State);
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
        harness.Runner.Start(new OperationDefinition(
            context => context.FrameClock.NextFrameAsync(context.CancellationToken),
            onCancel: async _ =>
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
        Assert.Empty(harness.Activities.Snapshot());
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
            IOperationRunner runner,
            IRuntimeActivityObserver activities)
        {
            Host = host;
            Clock = clock;
            Runner = runner;
            Activities = activities;
        }

        public RuntimeHost Host { get; }

        public ManualFrameClock Clock { get; }

        public IOperationRunner Runner { get; }

        public IRuntimeActivityObserver Activities { get; }

        public static async Task<OperationHarness> CreateAsync(IRuntimeLogger? logger = null)
        {
            var clock = new ManualFrameClock();
            var activities = new RuntimeActivityRegistry();
            var registry = new RuntimeServiceRegistry()
                .AddSingleton<IFrameClock>(_ => clock)
                .AddSingleton<IRuntimeActivityRegistry>(_ => activities)
                .AddSingleton<IRuntimeActivityObserver>(_ => activities)
                .AddSingleton<IOperationRunner>(
                    resolver => new OperationRunner(
                        resolver.Get<IFrameClock>(),
                    resolver.Get<IRuntimeLifetime>(),
                        logger,
                        resolver.Get<IRuntimeActivityRegistry>()),
                    typeof(IFrameClock),
                    typeof(IRuntimeLifetime),
                    typeof(IRuntimeActivityRegistry));
            var host = registry.Build(logger);
            await host.InitializeAsync();
            return new OperationHarness(
                host,
                clock,
                host.Services.Get<IOperationRunner>(),
                host.Services.Get<IRuntimeActivityObserver>());
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
