using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Resources;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task CaptureBackgroundBaselineAsync()
    {
        Assert(DisplayServer.GetName() != "headless", "Background baseline requires real GPU rendering.");
        var capture = Array.IndexOf(OS.GetCmdlineUserArgs(), "--majo-background-current") >= 0 ? "current" : "baseline";
        var directory = ProjectSettings.GlobalizePath("res://artifacts/background-actor/" + capture);
        Directory.CreateDirectory(directory);
        var owner = new Node { Name = "BackgroundBaseline" };
        AddChild(owner);
        var host = new GodotDirectionRenderHost(owner, new Vector2I(640, 360));
        await host.InitializeAsync(CancellationToken.None);
        using var pixels = Image.CreateEmpty(160, 120, false, Image.Format.Rgba8);
        pixels.Fill(Colors.Red);
        pixels.FillRect(new Rect2I(40, 0, 80, 120), Colors.Green);
        pixels.FillRect(new Rect2I(120, 0, 40, 120), Colors.Blue);
        using var texture = ImageTexture.CreateFromImage(pixels);
        try
        {
            foreach (var fit in Enum.GetValues<BackdropFit>())
            {
                host.PublishBackdrop(StageId.Main, new BackdropState("baseline", "background", "Default", fit, DirectionColor.White), CreateBackgroundRaster(host, texture));
                using var frame = await ReadTransitionFrameAsync(host.ScreenViewport);
                AssertColor(frame.GetPixel(frame.GetWidth() / 2, frame.GetHeight() / 2), Colors.Green, $"{fit} baseline center");
                Assert(frame.SavePng(Path.Combine(directory, $"fit-{fit}.png")) == Error.Ok, "Cannot save baseline frame.");
            }
            var placed = new BackdropState("baseline", "background", "Default")
            {
                Placement = new(new(50, 50), new(.5f, .5f), 2),
                Distance = 5,
            };
            host.PublishBackdrop(StageId.Main, placed, CreateBackgroundRaster(host, texture));
            using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
                Assert(frame.SavePng(Path.Combine(directory, "placement-distance.png")) == Error.Ok, "Cannot save placement frame.");
            var inset = new ViewId("inset");
            host.PublishView(inset, new ViewState(inset, StageId.Main, new ViewPose(new(55, 50), 1.2f, 5)), texture);
            host.PublishScreen(new ScreenState(new LayoutId("split"), new Dictionary<LayoutSlotId, ViewId>
            { [LayoutSlotId.Main] = ViewId.Main, [new("inset")] = inset }));
            using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
                Assert(frame.SavePng(Path.Combine(directory, "multi-view.png")) == Error.Ok, "Cannot save multi-view frame.");
            host.PublishScreen(ScreenState.Default);
            using var alternatePixels = Image.CreateEmpty(160, 120, false, Image.Format.Rgba8);
            alternatePixels.Fill(Colors.Blue);
            using var alternate = ImageTexture.CreateFromImage(alternatePixels);
            var stable = new BackdropState("baseline", "background", "Default");
            var drivers = new TransitionDriverRegistry();
            drivers.RegisterShader(new DirectionTransitionId("wipe"), GD.Load<Shader>("res://tests/headless/fixtures/background_wipe.gdshader"));
            foreach (var kind in new[] { DirectionTransitionId.Dissolve, new DirectionTransitionId("wipe") })
            {
                host.PublishBackdrop(StageId.Main, stable, CreateBackgroundRaster(host, alternate));
                host.PublishBackdropTransition(StageId.Main, stable, CreateBackgroundRaster(host, texture));
                var spec = new DirectionTransitionSpec(kind, 1);
                using var transition = host.PrepareBackdropVisualTransition(StageId.Main, spec,
                    drivers.GetRequired(kind));
                foreach (var progress in new[] { 0f, .5f, 1f })
                {
                    transition.ApplyProgress(progress);
                    using var frame = await ReadTransitionFrameAsync(host.ScreenViewport);
                    frame.SavePng(Path.Combine(directory, $"transition-{kind.Value}-{progress:0.0}.png"));
                }
                host.ClearBackdropTransition(StageId.Main);
            }
            host.PublishBackdrop(StageId.Main, stable, CreateBackgroundRaster(host, texture));
            host.GetStage(StageId.Main).BackdropFxRoot.Position = new Vector2(25, -10);
            using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
                frame.SavePng(Path.Combine(directory, "background-fx.png"));
            host.GetStage(StageId.Main).BackdropFxRoot.Position = Vector2.Zero;
            var windowSize = GetWindow().Size;
            GetWindow().Size = new Vector2I(900, 700);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
                frame.SavePng(Path.Combine(directory, "resize.png"));
            GetWindow().Size = windowSize;
            File.WriteAllText(Path.Combine(directory, "environment.txt"),
                $"Godot: {Engine.GetVersionInfo()}\nDisplay: {DisplayServer.GetName()}\n" +
                $"Rendering method: {RenderingServer.GetCurrentRenderingMethod()}\n" +
                $"Rendering driver: {RenderingServer.GetCurrentRenderingDriverName()}\n" +
                $"Metrics: {host.GetRenderResourceMetrics()}\n" +
                "Command: Godot --path . res://tests/headless/HeadlessTestRunner.tscn -- --majo-background-baseline\n");
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
            owner.QueueFree();
        }
    }
}
