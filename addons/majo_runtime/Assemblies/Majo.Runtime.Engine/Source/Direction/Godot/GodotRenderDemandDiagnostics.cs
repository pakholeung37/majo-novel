using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotRenderDemandDiagnostics(GodotDirectionRenderHost host) : IRenderDemandDiagnostics
{
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyList<ViewDemandSnapshot> InspectViewDemand() => host.InspectViewDemand()
        .Select(view => new ViewDemandSnapshot(view.View, view.Rendering, view.Screen, view.Requests, view.Sources)).ToArray();

    public IDisposable AcquireAllViews(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var requests = new List<IDisposable>();
        try
        {
            foreach (var id in host.ViewIds)
                requests.Add(host.AcquireViewDemand(host.RequireView(id), reason));
            return new DemandScope(requests);
        }
        catch
        {
            foreach (var request in requests) request.Dispose();
            throw;
        }
    }

    public IDisposable AcquireAllStageRasters(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return host.AcquireStageRasterDemand();
    }

    private sealed class DemandScope(List<IDisposable> requests) : IDisposable
    {
        public void Dispose()
        {
            foreach (var request in requests) request.Dispose();
            requests.Clear();
        }
    }
}
