using Majo.Runtime.Core;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeActivationTests
{
    [Fact]
    public async Task ActivationCanUseLateRegisteredServicesAndDeactivationPrecedesTheirShutdown()
    {
        var dependency = new ResourceService();
        var first = new Consumer();
        var second = new OtherConsumer();
        var order = new List<string>();
        first.OnDeactivate = () => order.Add("first");
        second.OnDeactivate = () => order.Add("second");
        await using var host = new RuntimeServiceRegistry()
            .AddSingleton(_ => first)
            .AddSingleton(_ => second)
            .AddSingleton(_ => dependency)
            .Build();

        await host.InitializeAsync();
        Assert.True(first.Activated && second.Activated);
        await host.ShutdownAsync();

        Assert.Equal(["second", "first"], order);
        Assert.Equal(1, first.Deactivations);
        Assert.Equal(1, second.Deactivations);
        Assert.False(dependency.IsOpen);
    }

    [Fact]
    public async Task FailedActivationReleasesPartialWorkAndEarlierActivationsBeforeServices()
    {
        var dependency = new ResourceService();
        var first = new Consumer();
        var failed = new OtherConsumer { FailActivation = true };
        var host = new RuntimeServiceRegistry()
            .AddSingleton(_ => first)
            .AddSingleton(_ => failed)
            .AddSingleton(_ => dependency)
            .Build();

        await Assert.ThrowsAsync<RuntimeLifecycleException>(() => host.InitializeAsync());

        Assert.Equal(1, first.Deactivations);
        Assert.Equal(1, failed.Deactivations);
        Assert.False(dependency.IsOpen);
        Assert.Equal(RuntimeHostState.Faulted, host.State);
        await host.ShutdownAsync();
        Assert.Equal(1, first.Deactivations);
        Assert.Equal(1, failed.Deactivations);
    }

    [Fact]
    public async Task ActivationThatIgnoresCancellationCannotPublishInitializedHost()
    {
        using var cancellation = new CancellationTokenSource();
        var consumer = new Consumer { OnActivate = cancellation.Cancel };
        var dependency = new ResourceService();
        var host = new RuntimeServiceRegistry()
            .AddSingleton(_ => consumer)
            .AddSingleton(_ => dependency).Build();

        await Assert.ThrowsAsync<RuntimeLifecycleException>(() => host.InitializeAsync(cancellation.Token));

        Assert.Equal(RuntimeHostState.Faulted, host.State);
        Assert.Equal(1, consumer.Deactivations);
        Assert.False(dependency.IsOpen);
    }

    [Fact]
    public async Task DeactivationFailureStillClosesServicesAndIsNotRetriedAgainstClosedDependencies()
    {
        var dependency = new ResourceService();
        var consumer = new Consumer { FailDeactivation = true };
        var host = new RuntimeServiceRegistry()
            .AddSingleton(_ => consumer).AddSingleton(_ => dependency).Build();
        await host.InitializeAsync();

        await Assert.ThrowsAsync<RuntimeLifecycleException>(() => host.ShutdownAsync());
        Assert.False(dependency.IsOpen);
        Assert.Equal(1, consumer.Deactivations);
        await host.ShutdownAsync();
        Assert.Equal(1, consumer.Deactivations);
        Assert.Equal(RuntimeHostState.Shutdown, host.State);
    }

    private sealed class ResourceService : IRuntimeService
    {
        public bool IsOpen { get; private set; }
        public Task InitializeAsync(CancellationToken token) { IsOpen = true; return Task.CompletedTask; }
        public Task ResetAsync(CancellationToken token) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken token) { IsOpen = false; return Task.CompletedTask; }
    }

    private class Consumer : IRuntimeService, IRuntimeActivation
    {
        private ResourceService? resource;
        public bool Activated { get; private set; }
        public bool FailActivation { get; init; }
        public bool FailDeactivation { get; init; }
        public int Deactivations { get; private set; }
        public Action? OnActivate { get; init; }
        public Action? OnDeactivate { get; set; }
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken token) => Task.CompletedTask;
        public Task ActivateAsync(IServiceResolver services, CancellationToken token)
        {
            resource = services.Get<ResourceService>();
            Assert.True(resource.IsOpen);
            Activated = true;
            OnActivate?.Invoke();
            if (FailActivation) throw new InvalidOperationException("activation failed");
            return Task.CompletedTask;
        }
        public Task DeactivateAsync(CancellationToken token)
        {
            Assert.True(resource!.IsOpen);
            Deactivations++;
            OnDeactivate?.Invoke();
            if (FailDeactivation) throw new InvalidOperationException("deactivation failed");
            return Task.CompletedTask;
        }
    }

    private sealed class OtherConsumer : Consumer { }
}
