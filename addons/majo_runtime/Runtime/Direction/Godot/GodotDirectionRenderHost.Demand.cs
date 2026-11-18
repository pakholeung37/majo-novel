#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed partial class GodotDirectionRenderHost
{
    private readonly GodotViewRenderDemand viewDemand = new();
    private CancellationTokenSource renderCancellation = new();
    private int screenRequests;
    private int stageRasterRequests;
    private bool outputActive;
    private long renderSizeRevision;

    internal IReadOnlyList<GodotViewRenderDemand.Snapshot> InspectViewDemand() => viewDemand.Inspect();

    internal GodotViewRenderDemand.Request AcquireViewDemand(GodotDirectionViewSurface view, string reason) =>
        viewDemand.Acquire(view, reason);

    internal IDisposable AcquireViewDependency(GodotDirectionViewSurface consumer, GodotDirectionViewSurface source) =>
        viewDemand.Depend(consumer, source);

    internal IDisposable AcquireBackdropTransitionDemand(GodotDirectionViewSurface view) =>
        viewDemand.AcquireBackdropTransition(view);

    private void RefreshScreenDemand()
    {
        var enabled = outputActive || screenRequests > 0;
        var mode = enabled ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (ScreenViewport.RenderTargetUpdateMode != mode) ScreenViewport.RenderTargetUpdateMode = mode;
        viewDemand.SetScreen(screen.Views, enabled);
    }

    private DemandLease AcquireScreenDemand()
    {
        var lifetime = renderCancellation;
        screenRequests++;
        RefreshScreenDemand();
        return new DemandLease(() =>
        {
            if (!ReferenceEquals(lifetime, renderCancellation) || shutdownRequested) return;
            screenRequests--;
            RefreshScreenDemand();
        });
    }

    internal IDisposable AcquireStageRasterDemand()
    {
        EnsureInitialized();
        var lifetime = renderCancellation;
        stageRasterRequests++;
        RefreshStageRasterDemand();
        return new DemandLease(() =>
        {
            if (!ReferenceEquals(lifetime, renderCancellation) || shutdownRequested) return;
            stageRasterRequests--;
            RefreshStageRasterDemand();
        });
    }

    private void RefreshStageRasterDemand()
    {
        var mode = stageRasterRequests > 0 ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        foreach (var stage in surfaces.Stages) stage.Viewport.RenderTargetUpdateMode = mode;
    }

    private void ResetRenderDemand()
    {
        var previous = renderCancellation;
        renderCancellation = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        screenRequests = 0;
        stageRasterRequests = 0;
        RefreshStageRasterDemand();
        viewDemand.Clear();
        viewDemand.Register(surfaces.MainView);
    }

    internal async Task WaitForRenderedViewAsync(GodotDirectionViewSurface view,
        GodotViewRenderDemand.Request demand, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, renderCancellation.Token);
        long revision;
        do
        {
            revision = renderSizeRevision;
            await GodotVisualInput.WaitForFrameAsync(owner.GetTree(), lifetime.Token);
            if (!demand.IsValid || !GodotObject.IsInstanceValid(view.Viewport))
                throw new DirectionException("Direction View was released while waiting for rendering.");
        } while (revision != renderSizeRevision);
    }

    private async Task WaitForRenderedScreenAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, renderCancellation.Token);
        long revision;
        do
        {
            revision = renderSizeRevision;
            await GodotVisualInput.WaitForFrameAsync(owner.GetTree(), lifetime.Token);
        } while (revision != renderSizeRevision);
    }

    private sealed class DemandLease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose()
        {
            var callback = release;
            release = null;
            callback?.Invoke();
        }
    }
}
