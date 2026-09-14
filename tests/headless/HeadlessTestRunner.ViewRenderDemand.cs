using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateViewRenderDemandAsync()
    {
        var owner = new Node { Name = "ViewDemandProbe" };
        AddChild(owner);
        using var host = new GodotDirectionRenderHost(owner, new Vector2I(640, 360));
        await host.InitializeAsync(CancellationToken.None);
        using var texture = new PlaceholderTexture2D { Size = new Vector2(640, 360) };
        var secondId = new ViewId("demand-second");
        var second = host.GetOrCreateView(secondId);
        var main = host.RequireView(ViewId.Main);
        var state = new ViewState(secondId, StageId.Main, ViewPose.Default);
        host.PublishView(secondId, state, texture);
        var secondScreen = new ScreenState(ScreenState.Default.Layout,
            new Dictionary<LayoutSlotId, ViewId> { [LayoutSlotId.Main] = secondId });
        try
        {
            AssertViewRendering(main, true, "Initial screen");
            AssertViewRendering(second, false, "Preparing an unused View must not render it");
            using (var first = host.AcquireViewDemand(second, "capture one"))
            {
                var other = host.AcquireViewDemand(second, "capture two");
                first.Dispose();
                AssertViewRendering(second, true, "Overlapping captures");
                host.PublishScreen(secondScreen);
                other.Dispose();
                AssertViewRendering(second, true, "Screen demand outlives capture");
                AssertViewRendering(main, false, "Unused permanent main View");
            }
            var live = host.CreateLiveViewSource(secondId, state);
            using (var preparation = host.AcquireViewDemand(live, "prepare"))
            using (var dependency = host.AcquireViewDependency(second, live))
            {
                preparation.Dispose();
                AssertViewRendering(live, true, "Live camera source follows visible consumer");
                host.PublishScreen(ScreenState.Default);
                AssertViewRendering(live, false, "Hidden transition is not an independent root");
                using (host.AcquireViewDemand(second, "capture transition"))
                    AssertViewRendering(live, true, "Capture requests the dependency closure");
                AssertViewRendering(live, false, "Capture closure releases");
                // Duplicate dependencies have independent ownership.
                using (host.AcquireViewDependency(second, live))
                {
                    dependency.Dispose();
                    host.PublishScreen(secondScreen);
                    AssertViewRendering(live, true, "Releasing one dependency preserves another");
                }
                AssertViewRendering(live, false, "Last dependency releases");
            }
            host.ReleaseLiveViewSource(live);

            // Existing slots choose target bindings; only disappearing slots retain old inputs.
            host.PublishScreenLayout(secondScreen, ScreenState.Default, .5f);
            AssertViewRendering(main, false, "Rebound slot must not keep old View rendering");
            var extraSlot = new LayoutSlotId("extra");
            var before = new ScreenState(ScreenState.Default.Layout,
                new Dictionary<LayoutSlotId, ViewId> { [LayoutSlotId.Main] = secondId, [extraSlot] = ViewId.Main });
            host.PublishScreenLayout(secondScreen, before, .5f);
            AssertViewRendering(main, true, "Disappearing slot still consumes its View");
            host.PublishScreenLayout(secondScreen, before, 1);
            AssertViewRendering(main, false, "Finished disappearing slot releases its View");

            foreach (var layout in new[] { "split", "pip" })
            {
                host.PublishScreen(new ScreenState(new LayoutId(layout), new Dictionary<LayoutSlotId, ViewId>
                { [LayoutSlotId.Main] = ViewId.Main, [new LayoutSlotId("inset")] = secondId }));
                AssertViewRendering(main, true, layout + " main");
                AssertViewRendering(second, true, layout + " inset");
            }
            host.PublishScreen(new ScreenState(new LayoutId("pip"), new Dictionary<LayoutSlotId, ViewId>
            { [LayoutSlotId.Main] = ViewId.Main, [new LayoutSlotId("inset")] = ViewId.Main }));
            AssertViewRendering(second, false, "Two slots sharing main must not retain the second View");
            host.PublishScreen(secondScreen);

            var stale = host.AcquireViewDemand(second, "old registration");
            host.ReleasePreparedView(secondId);
            var replacement = host.GetOrCreateView(secondId);
            using (host.AcquireViewDemand(replacement, "new registration"))
            {
                stale.Dispose();
                AssertViewRendering(replacement, true, "Reused View ID is independent of old tokens");
            }
            AssertViewRendering(replacement, false, "Replacement capture releases");
            var oldMain = host.AcquireViewDemand(main, "before reset");
            await host.ResetAsync(CancellationToken.None);
            using (host.AcquireViewDemand(main, "after reset"))
            {
                oldMain.Dispose();
                AssertViewRendering(main, true, "Reset reuses main surface with new registration");
            }
            AssertViewRendering(main, false, "Full reset output is inactive");
            using (var screen = await host.CaptureScreenAsync(CancellationToken.None)) { }
            AssertViewRendering(main, false, "Hidden screen capture releases its roots");

            host.PublishView(secondId, state, texture);
            second = host.RequireView(secondId);
            using var cancelled = new CancellationTokenSource();
            var capture = host.CaptureViewAsync(secondId, cancelled.Token);
            cancelled.Cancel();
            await ExpectBackgroundFailureAsync<OperationCanceledException>(async () => { using var image = await capture; });
            AssertViewRendering(second, false, "Cancelled capture releases all five passes");

            var resetCapture = host.CaptureViewAsync(secondId, CancellationToken.None);
            await host.ResetAsync(CancellationToken.None);
            await ExpectBackgroundFailureAsync<OperationCanceledException>(async () => { using var image = await resetCapture; });
            Assert(host.InspectViewDemand().All(view => view.Requests.Length == 0), "Reset leaked render requests.");
            if (DisplayServer.GetName() != "headless")
                await ValidateHiddenViewPixelsAsync(host, texture, secondId, secondScreen);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            owner.QueueFree();
        }
        if (DisplayServer.GetName() != "headless") await ValidateViewDemandResizeAsync();
    }

    private async Task ValidateHiddenViewPixelsAsync(GodotDirectionRenderHost host, Texture2D texture,
        ViewId secondId, ScreenState secondScreen)
    {
        host.PublishView(secondId, new ViewState(secondId, StageId.Main, ViewPose.Default), texture);
        var second = host.RequireView(secondId);
        // A native actor-owned producer, with Stage still ordered under disabled main Content.
        var producer = new SubViewport
        {
            Size = new Vector2I(640, 360), TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        host.Actors.AddChild(producer);
        var color = new ColorRect { Size = new Vector2(640, 360), Color = Colors.Red };
        producer.AddChild(color);
        var actor = new TextureRect { Texture = producer.GetTexture(), Size = new Vector2(640, 360) };
        host.Actors.AddChild(actor);
        GodotDirectionRenderHost.AssignVisibilityLayer(actor, GodotDirectionRenderHost.SubjectVisibilityLayer);
        using (var image = await host.CaptureViewAsync(secondId, CancellationToken.None))
        using (var pixels = image.GetImage())
            AssertBackgroundPixel(pixels, Colors.Red, "Cold hidden capture refreshes every pass");
        AssertViewRendering(second, false, "Hidden capture stops rendering afterward");
        color.Color = Colors.Blue;
        using (var image = await host.CaptureViewAsync(secondId, CancellationToken.None))
        using (var pixels = image.GetImage())
            AssertBackgroundPixel(pixels, Colors.Blue, "Second hidden capture must not reuse stale pixels");
        host.PublishScreen(secondScreen);
        AssertViewRendering(host.RequireView(ViewId.Main), false, "Native producer test requires disabled main View");
        for (var frame = 0; frame < 6; frame++)
        {
            color.Color = frame % 2 == 0 ? Colors.Green : Colors.Red;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = host.ScreenViewport.GetTexture().GetImage();
            AssertBackgroundPixel(image, color.Color, $"Native actor producer ordering on frame {frame}");
        }
        GD.Print("View demand: hidden captures, native actor producer ordering and request lifecycle passed.");
    }

    private async Task ValidateViewDemandResizeAsync()
    {
        var viewport = new SubViewport { Size = new Vector2I(128, 72), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(viewport);
        var owner = new Node();
        viewport.AddChild(owner);
        using var host = new GodotDirectionRenderHost(owner, Vector2I.Zero);
        await host.InitializeAsync(CancellationToken.None);
        try
        {
            await host.ResetAsync(CancellationToken.None);
            var capture = host.CaptureViewAsync(ViewId.Main, CancellationToken.None);
            viewport.Size = new Vector2I(256, 144);
            using var image = await capture;
            Assert(image.GetSize() == new Vector2(256, 144), "Resize during capture returned an old-size image.");
            AssertViewRendering(host.RequireView(ViewId.Main), false, "Resize capture releases temporary demand");
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            viewport.QueueFree();
        }
    }

    private static void AssertViewRendering(GodotDirectionViewSurface view, bool expected, string context)
    {
        var expectedMode = expected ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        Assert(new[] { view.Viewport, view.ContentViewport, view.BackgroundViewport,
            view.TransitionBackgroundViewport, view.CompositeViewport }.All(surface => surface.RenderTargetUpdateMode == expectedMode),
            $"{context}: View {view.Id} must have all five passes {expectedMode}.");
    }
}
