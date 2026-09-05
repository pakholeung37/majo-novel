#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class RuntimeHostLifecycleTests
{
    private interface IServiceA : IRuntimeService { }
    private interface IServiceB : IRuntimeService { }

    [Fact(Timeout = 5_000)]
    public async Task RuntimeHost_ResetAndShutdown_OrderingIsDeterministic()
    {
        var resetOrder = new List<string>();
        var shutdownOrder = new List<string>();

        var serviceA = new OrderedLoggingServiceA("ServiceA", resetOrder, shutdownOrder);
        var serviceB = new OrderedLoggingServiceB("ServiceB", resetOrder, shutdownOrder);

        var registry = new RuntimeServiceRegistry()
            .AddSingleton<IServiceA>(_ => serviceA)
            .AddSingleton<IFrameClock>(_ => new StubFrameClock())
            .AddSingleton<IServiceB>(
                _ => serviceB,
                typeof(IServiceA)); // serviceB depends on serviceA

        var host = registry.Build();
        await host.InitializeAsync();

        await host.ResetAsync();
        Assert.NotEmpty(resetOrder);

        await host.ShutdownAsync();
        Assert.NotEmpty(shutdownOrder);

        // Shutdown is reverse topological order
        var aIndex = shutdownOrder.IndexOf("ServiceA");
        var bIndex = shutdownOrder.IndexOf("ServiceB");
        Assert.True(bIndex < aIndex, "Dependent service B should shut down before prerequisite service A.");
    }

    private sealed class OrderedLoggingServiceA : IServiceA
    {
        private readonly string name;
        private readonly List<string> resetList;
        private readonly List<string> shutdownList;

        public OrderedLoggingServiceA(string name, List<string> resetList, List<string> shutdownList)
        {
            this.name = name;
            this.resetList = resetList;
            this.shutdownList = shutdownList;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            resetList.Add(name);
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            shutdownList.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class OrderedLoggingServiceB : IServiceB
    {
        private readonly string name;
        private readonly List<string> resetList;
        private readonly List<string> shutdownList;

        public OrderedLoggingServiceB(string name, List<string> resetList, List<string> shutdownList)
        {
            this.name = name;
            this.resetList = resetList;
            this.shutdownList = shutdownList;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            resetList.Add(name);
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            shutdownList.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class StubFrameClock : IFrameClock
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NextFrameAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
