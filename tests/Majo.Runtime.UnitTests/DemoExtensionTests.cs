using Majo.Demo;
using Majo.Runtime.Core;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DemoExtensionTests
{
    [Fact]
    public async Task CaseServiceTracksLifecycleAndClearsRouteOnReset()
    {
        using var lifetime = new TestRuntimeLifetime();
        var service = new DemoSessionService(lifetime);

        await service.InitializeAsync(CancellationToken.None);
        service.SelectRoute("evidence");

        Assert.Equal("evidence", service.SelectedRoute);
        Assert.Equal(1, service.InitializeCount);

        await service.ResetAsync(CancellationToken.None);

        Assert.Null(service.SelectedRoute);
        Assert.Equal(1, service.ResetCount);

        lifetime.CancelRuntime();
        await service.ShutdownAsync(CancellationToken.None);

        Assert.Equal(1, service.ShutdownCount);
        Assert.True(service.RuntimeWasCancelledAtShutdown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void CaseServiceRejectsEmptyRoute(string route)
    {
        using var lifetime = new TestRuntimeLifetime();
        var service = new DemoSessionService(lifetime);

        Assert.Throws<ArgumentException>(() => service.SelectRoute(route));
    }

    private sealed class TestRuntimeLifetime : IRuntimeLifetime, IDisposable
    {
        private readonly CancellationTokenSource runtime = new();
        private readonly CancellationTokenSource session = new();

        public CancellationToken RuntimeCancellationToken => runtime.Token;

        public CancellationToken SessionCancellationToken => session.Token;

        public void CancelRuntime() => runtime.Cancel();

        public void Dispose()
        {
            runtime.Dispose();
            session.Dispose();
        }
    }
}
