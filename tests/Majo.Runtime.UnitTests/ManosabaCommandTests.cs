#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Demos.Manosaba;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ManosabaCommandTests
{
    [Fact]
    public async Task StreamFrameCommand_TogglesModes()
    {
        var uiService = new ManosabaUiService();
        var services = new ServiceResolverBuilder()
            .Add<IManosabaUiService>(uiService)
            .Build();

        var registry = new RuntimeCommandRegistry();
        ManosabaCommands.Register(registry);
        registry.SealRegistrations();
        var catalog = registry.BuildCatalog(services);

        var dispatcher = new RuntimeCommandDispatcher(services: services);

        var streamFrame = catalog.Get("stream_frame");

        // stream_frame true -> Stream
        await dispatcher.ExecuteAsync(streamFrame, new CommandInput("stream_frame", ["true"]), CancellationToken.None);
        Assert.Equal(ManosabaFrameMode.Stream, uiService.CurrentFrameMode);

        // stream_frame false -> Default
        await dispatcher.ExecuteAsync(streamFrame, new CommandInput("stream_frame", ["false"]), CancellationToken.None);
        Assert.Equal(ManosabaFrameMode.Default, uiService.CurrentFrameMode);
    }

    [Fact]
    public async Task ManosabaUiService_StatePersistence_CapturesAndRestores()
    {
        var uiService = new ManosabaUiService();
        uiService.SetFrameMode(ManosabaFrameMode.Stream);

        var record = await uiService.CaptureAsync(CancellationToken.None);
        Assert.Equal("manosaba.ui", record.Key);
        Assert.Equal(StateScope.Game, record.Scope);

        uiService.SetFrameMode(ManosabaFrameMode.Default);
        Assert.Equal(ManosabaFrameMode.Default, uiService.CurrentFrameMode);

        await uiService.PreflightAsync(record, CancellationToken.None);
        await uiService.CommitPreparedAsync(record, CancellationToken.None);
        Assert.Equal(ManosabaFrameMode.Stream, uiService.CurrentFrameMode);

        await ((IStateContributor)uiService).ResetAsync(CancellationToken.None);
        Assert.Equal(ManosabaFrameMode.Default, uiService.CurrentFrameMode);
    }
}

internal sealed class ServiceResolverBuilder
{
    private readonly Dictionary<Type, object> services = new();

    public ServiceResolverBuilder Add<TService>(TService instance) where TService : class
    {
        services[typeof(TService)] = instance;
        return this;
    }

    public IServiceResolver Build() => new SimpleServiceResolver(services);

    private sealed class SimpleServiceResolver : IServiceResolver
    {
        private readonly Dictionary<Type, object> services;

        public SimpleServiceResolver(Dictionary<Type, object> services)
        {
            this.services = services;
        }

        public TService Resolve<TService>() where TService : class
        {
            if (TryGet<TService>(out var service) && service is not null)
            {
                return service;
            }

            throw new InvalidOperationException($"Service '{typeof(TService).FullName}' not found.");
        }

        public object Resolve(Type serviceType)
        {
            if (services.TryGetValue(serviceType, out var obj))
            {
                return obj;
            }

            throw new InvalidOperationException($"Service '{serviceType.FullName}' not found.");
        }

        public bool TryGet<TService>(out TService? service) where TService : class
        {
            if (services.TryGetValue(typeof(TService), out var obj) && obj is TService typed)
            {
                service = typed;
                return true;
            }

            service = null;
            return false;
        }
    }
}
