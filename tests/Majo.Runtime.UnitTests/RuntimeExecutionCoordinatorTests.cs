using Majo.Runtime.Core;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeExecutionCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedBackendResetsOnceAndOrdinaryServicesStillResetAfterCoordinatorFailure(bool failReset)
    {
        var backend = new OwnedBackend();
        var other = new OtherBackend();
        var coordinator = new Coordinator(backend) { FailReset = failReset };
        await using var host = new RuntimeServiceRegistry()
            .AddSingleton(_ => backend)
            .AddSingleton(_ => other)
            .AddSingleton<IRuntimeExecutionCoordinator>(_ => coordinator)
            .Build();
        await host.InitializeAsync();

        var previousSession = host.SessionCancellationToken;
        if (failReset)
            await Assert.ThrowsAsync<RuntimeLifecycleException>(() => host.ResetAsync());
        else
            await host.ResetAsync();

        Assert.True(previousSession.IsCancellationRequested);
        Assert.Equal(1, coordinator.Resets);
        Assert.Equal(1, backend.Resets);
        Assert.Equal(1, other.Resets);
        Assert.Equal(failReset ? RuntimeHostState.Faulted : RuntimeHostState.Initialized, host.State);
    }

    [Fact]
    public async Task RestoreCapabilitiesAreDelegatedToTheRegisteredCoordinator()
    {
        var coordinator = new Coordinator(new OwnedBackend());
        await using var host = new RuntimeServiceRegistry()
            .AddSingleton<IRuntimeExecutionCoordinator>(_ => coordinator).Build();
        await host.InitializeAsync();
        await host.CancelForRestoreAsync();
        await host.CancelForHotReloadAsync(default);
        Assert.Equal(1, coordinator.Restores);
        Assert.Equal(1, coordinator.HotReloads);
        Assert.Same(coordinator.Output, host.RetainRestoreOutput());
    }

    private class OwnedBackend : IRuntimeService
    {
        public int Resets { get; private set; }
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token) { Resets++; return Task.CompletedTask; }
        public Task ShutdownAsync(CancellationToken token) => Task.CompletedTask;
    }

    private sealed class OtherBackend : OwnedBackend { }

    private sealed class Coordinator(OwnedBackend backend) : IRuntimeExecutionCoordinator
    {
        public IReadOnlyCollection<Type> ManagedResetServices { get; } = [typeof(OwnedBackend)];
        public bool FailReset { get; init; }
        public int Resets { get; private set; }
        public int Restores { get; private set; }
        public int HotReloads { get; private set; }
        public OutputLease Output { get; } = new();
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public async Task ResetAsync(CancellationToken token)
        {
            Resets++;
            await backend.ResetAsync(token);
            if (FailReset) throw new InvalidOperationException("backend reset failed");
        }
        public Task ShutdownAsync(CancellationToken token) => Task.CompletedTask;
        public IRetainedRestoreOutput RetainRestoreOutput() => Output;
        public Task CancelForRestoreAsync(CancellationToken token) { Restores++; return Task.CompletedTask; }
        public Task CancelForHotReloadAsync(CancellationToken token) { HotReloads++; return Task.CompletedTask; }
        public Task WaitForQuiescenceAsync(CancellationToken token) => Task.CompletedTask;
    }

    private sealed class OutputLease : IRetainedRestoreOutput
    {
        public Task WaitForTargetAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
}
