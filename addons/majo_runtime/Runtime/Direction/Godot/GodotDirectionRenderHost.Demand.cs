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
    private readonly HashSet<GodotDirectionViewSurface> screenViews = new();
    private CancellationTokenSource renderCancellation = new();
    private int screenRequests;
    private bool outputActive;
    private long renderSizeRevision;

    internal IReadOnlyList<GodotViewRenderDemand.Snapshot> InspectViewDemand() => viewDemand.Inspect();

    internal GodotViewRenderDemand.Request AcquireViewDemand(GodotDirectionViewSurface view, string reason) =>
        viewDemand.Acquire(view, reason);

    internal IDisposable AcquireViewDependency(GodotDirectionViewSurface consumer, GodotDirectionViewSurface source) =>
        viewDemand.Depend(consumer, source);

    private void RefreshScreenDemand()
    {
        var enabled = outputActive || screenRequests > 0;
        var mode = enabled ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (ScreenViewport.RenderTargetUpdateMode != mode) ScreenViewport.RenderTargetUpdateMode = mode;
        viewDemand.SetScreen(screenViews, enabled);
    }

    private ScreenDemand AcquireScreenDemand()
    {
        var lifetime = renderCancellation;
        screenRequests++;
        RefreshScreenDemand();
        return new ScreenDemand(() =>
        {
            if (!ReferenceEquals(lifetime, renderCancellation) || shutdownRequested) return;
            screenRequests--;
            RefreshScreenDemand();
        });
    }

    private void ResetRenderDemand()
    {
        var previous = renderCancellation;
        renderCancellation = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        screenRequests = 0;
        viewDemand.Clear();
        screenViews.Clear();
        viewDemand.Register(views[ViewId.Main]);
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

    private sealed class ScreenDemand(Action release) : IDisposable
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
