using Majo.Runtime.Core;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeServiceRegistryTests
{
    [Fact(Timeout = 5_000)]
    public async Task HostInitializesDependenciesAndShutsDownInReverseOrder()
    {
        var events = new List<string>();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<ServiceA>(
                resolver => new ServiceA(resolver.Get<ServiceB>(), events),
                typeof(ServiceB))
            .AddSingleton<ServiceB>(
                resolver => new ServiceB(resolver.Get<ServiceC>(), events),
                typeof(ServiceC))
            .AddSingleton<ServiceC>(_ => new ServiceC(events));
        var host = registry.Build();

        await host.InitializeAsync();
        Assert.Equal(
            ["initialize:C", "initialize:B", "initialize:A"],
            events);

        await host.ShutdownAsync();
        Assert.Equal(
            [
                "initialize:C",
                "initialize:B",
                "initialize:A",
                "shutdown:A",
                "shutdown:B",
                "shutdown:C",
            ],
            events);
    }

    [Fact]
    public void BuildRejectsMissingDependencyWithFullChain()
    {
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<ServiceA>(
                _ => new ServiceA(null, []),
                typeof(ServiceB));

        var exception = Assert.Throws<MissingServiceDependencyException>(() => registry.Build());

        Assert.Equal([typeof(ServiceA), typeof(ServiceB)], exception.DependencyChain);
        Assert.Contains(nameof(ServiceA), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ServiceB), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryRejectsDuplicateRegistration()
    {
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<ServiceC>(_ => new ServiceC([]));

        var exception = Assert.Throws<DuplicateServiceRegistrationException>(() =>
            registry.AddSingleton<ServiceC>(_ => new ServiceC([])));

        Assert.Contains(nameof(ServiceC), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsCircularDependencyWithCyclePath()
    {
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<ServiceA>(
                _ => new ServiceA(null, []),
                typeof(ServiceB))
            .AddSingleton<ServiceB>(
                _ => new ServiceB(null, []),
                typeof(ServiceA));

        var exception = Assert.Throws<CircularServiceDependencyException>(() => registry.Build());

        Assert.Equal(
            [typeof(ServiceA), typeof(ServiceB), typeof(ServiceA)],
            exception.DependencyChain);
    }

    [Fact(Timeout = 5_000)]
    public async Task InitializationFailureCleansUpOnlyInitializedServicesInReverseOrder()
    {
        var events = new List<string>();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<FailingService>(
                resolver => new FailingService(resolver.Get<ServiceC>(), events),
                typeof(ServiceC))
            .AddSingleton<ServiceC>(_ => new ServiceC(events));
        var host = registry.Build();

        var exception = await Assert.ThrowsAsync<RuntimeLifecycleException>(
            () => host.InitializeAsync());

        Assert.Equal(RuntimeHostState.Faulted, host.State);
        Assert.Equal(
            ["initialize:C", "initialize:failing", "shutdown:failing", "shutdown:C"],
            events);
        Assert.Contains(nameof(FailingService), exception.ToString(), StringComparison.Ordinal);

        await host.ShutdownAsync();
        Assert.Equal(RuntimeHostState.Shutdown, host.State);
    }

    [Fact(Timeout = 5_000)]
    public async Task RepeatedLifecycleCallsHaveDeterministicResults()
    {
        var service = new CountingService();
        var host = new RuntimeServiceRegistry()
            .AddSingleton<CountingService>(_ => service)
            .Build();

        await host.InitializeAsync();
        await host.InitializeAsync();
        await host.ResetAsync();
        await host.ResetAsync();
        await host.ShutdownAsync();
        await host.ShutdownAsync();

        Assert.Equal(1, service.InitializeCount);
        Assert.Equal(2, service.ResetCount);
        Assert.Equal(1, service.ShutdownCount);
        Assert.Equal(RuntimeHostState.Shutdown, host.State);
        await Assert.ThrowsAsync<RuntimeLifecycleException>(() => host.ResetAsync());
    }

    [Fact(Timeout = 5_000)]
    public async Task ResetAndShutdownPropagateLifetimeCancellation()
    {
        var lifetimeConsumer = new LifetimeConsumer();
        var host = new RuntimeServiceRegistry()
            .AddSingleton<LifetimeConsumer>(
                resolver => lifetimeConsumer.Attach(resolver.Get<IRuntimeLifetime>()),
                typeof(IRuntimeLifetime))
            .Build();
        var firstSession = host.SessionCancellationToken;
        var runtime = host.RuntimeCancellationToken;

        await host.InitializeAsync();
        Assert.Same(host, lifetimeConsumer.Lifetime);
        Assert.Equal(firstSession, lifetimeConsumer.Lifetime!.SessionCancellationToken);
        await host.ResetAsync();

        Assert.True(firstSession.IsCancellationRequested);
        Assert.False(host.SessionCancellationToken.IsCancellationRequested);
        Assert.False(runtime.IsCancellationRequested);
        var secondSession = host.SessionCancellationToken;

        await host.ShutdownAsync();

        Assert.True(secondSession.IsCancellationRequested);
        Assert.True(runtime.IsCancellationRequested);
    }

    [Fact(Timeout = 5_000)]
    public async Task ThirdPartyCanAppendAndReplaceRegistrationsBeforeBuild()
    {
        var original = new OriginalExtensionService();
        var replacement = new ReplacementExtensionService();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<IExtensionService>(_ => original)
            .AddSingleton<ExtensionConsumer>(
                resolver => new ExtensionConsumer(resolver.Get<IExtensionService>()),
                typeof(IExtensionService))
            .ReplaceSingleton<IExtensionService>(_ => replacement);
        var host = registry.Build();

        await host.InitializeAsync();

        Assert.Same(replacement, host.Services.Get<IExtensionService>());
        Assert.Same(replacement, host.Services.Get<ExtensionConsumer>().Dependency);
        Assert.Equal(0, original.InitializeCount);
        Assert.Equal(1, replacement.InitializeCount);

        await host.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task FactoryCannotResolveUndeclaredDependency()
    {
        var events = new List<string>();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<ServiceC>(_ => new ServiceC(events))
            .AddSingleton<ServiceA>(
                resolver => new ServiceA(resolver.Get<ServiceC>(), events));
        var host = registry.Build();

        var exception = await Assert.ThrowsAsync<RuntimeLifecycleException>(
            () => host.InitializeAsync());

        Assert.Contains("undeclared", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(ServiceA), exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(nameof(ServiceC), exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(["initialize:C", "shutdown:C"], events);
    }

    [Fact(Timeout = 5_000)]
    public async Task ShutdownRetainsFailedServiceAndCanRetryCleanup()
    {
        var events = new List<string>();
        var registry = new RuntimeServiceRegistry()
            .AddSingleton<FailingShutdownService>(
                resolver => new FailingShutdownService(resolver.Get<ServiceC>(), events),
                typeof(ServiceC))
            .AddSingleton<ServiceC>(_ => new ServiceC(events));
        var host = registry.Build();
        await host.InitializeAsync();

        var exception = await Assert.ThrowsAsync<RuntimeLifecycleException>(
            () => host.ShutdownAsync());

        Assert.Equal(RuntimeHostState.Faulted, host.State);
        Assert.Contains(nameof(FailingShutdownService), exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            [
                "initialize:C",
                "initialize:failing-shutdown",
                "shutdown:failing-shutdown",
                "shutdown:C",
            ],
            events);

        await host.ShutdownAsync();
        Assert.Equal(RuntimeHostState.Shutdown, host.State);
        Assert.Equal("shutdown:failing-shutdown", events[^1]);
        Assert.Equal(5, events.Count);
    }

    private abstract class RecordingService : IRuntimeService
    {
        private readonly string name;
        private readonly IList<string> events;

        protected RecordingService(string name, IList<string> events)
        {
            this.name = name;
            this.events = events;
        }

        public virtual Task InitializeAsync(CancellationToken cancellationToken)
        {
            events.Add($"initialize:{name}");
            return Task.CompletedTask;
        }

        public virtual Task ResetAsync(CancellationToken cancellationToken)
        {
            events.Add($"reset:{name}");
            return Task.CompletedTask;
        }

        public virtual Task ShutdownAsync(CancellationToken cancellationToken)
        {
            events.Add($"shutdown:{name}");
            return Task.CompletedTask;
        }
    }

    private sealed class ServiceA : RecordingService
    {
        public ServiceA(object? dependency, IList<string> events)
            : base("A", events)
        {
            Dependency = dependency;
        }

        public object? Dependency { get; }
    }

    private sealed class ServiceB : RecordingService
    {
        public ServiceB(ServiceC? dependency, IList<string> events)
            : base("B", events)
        {
            Dependency = dependency;
        }

        public ServiceC? Dependency { get; }
    }

    private sealed class ServiceC : RecordingService
    {
        public ServiceC(IList<string> events)
            : base("C", events)
        {
        }
    }

    private sealed class FailingService : RecordingService
    {
        public FailingService(ServiceC dependency, IList<string> events)
            : base("failing", events)
        {
            Dependency = dependency;
        }

        public ServiceC Dependency { get; }

        public override Task InitializeAsync(CancellationToken cancellationToken)
        {
            base.InitializeAsync(cancellationToken);
            throw new InvalidOperationException("Expected initialization failure.");
        }
    }

    private sealed class FailingShutdownService : RecordingService
    {
        private bool hasFailed;

        public FailingShutdownService(ServiceC dependency, IList<string> events)
            : base("failing-shutdown", events)
        {
            Dependency = dependency;
        }

        public ServiceC Dependency { get; }

        public override Task ShutdownAsync(CancellationToken cancellationToken)
        {
            base.ShutdownAsync(cancellationToken);
            if (!hasFailed)
            {
                hasFailed = true;
                throw new InvalidOperationException("Expected shutdown failure.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CountingService : IRuntimeService
    {
        public int InitializeCount { get; private set; }

        public int ResetCount { get; private set; }

        public int ShutdownCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            ResetCount++;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class LifetimeConsumer : IRuntimeService
    {
        public IRuntimeLifetime? Lifetime { get; private set; }

        public LifetimeConsumer Attach(IRuntimeLifetime lifetime)
        {
            Lifetime = lifetime;
            return this;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private interface IExtensionService : IRuntimeService
    {
        int InitializeCount { get; }
    }

    private class OriginalExtensionService : IExtensionService
    {
        public int InitializeCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ReplacementExtensionService : OriginalExtensionService
    {
    }

    private sealed class ExtensionConsumer : IRuntimeService
    {
        public ExtensionConsumer(IExtensionService dependency)
        {
            Dependency = dependency;
        }

        public IExtensionService Dependency { get; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
