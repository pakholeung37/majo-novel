using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateBackgroundAlphaAsync()
    {
        Assert(DisplayServer.GetName() != "headless", "Background alpha checks require GPU rendering.");
        var directory = ProjectSettings.GlobalizePath("res://artifacts/background-actor/alpha");
        Directory.CreateDirectory(directory);
        var owner = new Node();
        AddChild(owner);
        using var host = new GodotDirectionRenderHost(owner, new Vector2I(64, 64));
        await host.InitializeAsync(default);
        using var redPixels = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8);
        redPixels.Fill(new Color(1, 0, 0, .5f));
        using var redTexture = ImageTexture.CreateFromImage(redPixels);
        using var bluePixels = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8);
        bluePixels.Fill(new Color(0, 0, 1, .5f));
        using var blueTexture = ImageTexture.CreateFromImage(bluePixels);
        using var redContent = new GodotBackgroundContent(new Sprite2D { Texture = redTexture, Centered = false },
            new Rect2(0, 0, 64, 64), BackgroundCoverage.Unknown);
        using var blueContent = new GodotBackgroundContent(new Sprite2D { Texture = blueTexture, Centered = false },
            new Rect2(0, 0, 64, 64), BackgroundCoverage.Unknown);
        using var red = new GodotBackgroundRaster(owner, redContent);
        using var blue = new GodotBackgroundRaster(owner, blueContent);
        try
        {
            await red.ReadyAsync(default);
            using var frozenRed = red.Freeze();
            using var frozenBlue = blue.Freeze();
            var state = new BackdropState("alpha", "fixture", "Default") { Tint = new DirectionColor(1, 1, 1, .5f) };
            host.PublishBackdrop(StageId.Main, state, red);
            using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
            {
                AssertBackgroundPixel(frame, new Color(.25f, 0, 0), "Stable content alpha and Stage tint must apply once.");
                frame.SavePng(Path.Combine(directory, "stable.png"));
            }
            var drivers = new TransitionDriverRegistry();
            var wipe = new DirectionTransitionId("wipe");
            drivers.RegisterShader(wipe, GD.Load<Shader>("res://tests/headless/fixtures/background_wipe.gdshader"));
            foreach (var kind in new[] { DirectionTransitionId.Dissolve, wipe })
            {
                host.PublishBackdrop(StageId.Main, state, blue);
                host.PublishBackdropTransition(StageId.Main, state, red);
                using var mix = host.PrepareBackdropVisualTransition(StageId.Main, new DirectionTransitionSpec(kind, 1), drivers.GetRequired(kind));
                foreach (var p in new[] { 0f, .5f, 1f })
                {
                    mix.ApplyProgress(p);
                    using var frame = await ReadTransitionFrameAsync(host.ScreenViewport);
                    if (kind == DirectionTransitionId.Dissolve)
                        AssertBackgroundPixel(frame, new Color(.25f * (1 - p), 0, .25f * p), "Dissolve must blend premultiplied inputs exactly once.");
                    else if (p != .5f) AssertBackgroundPixel(frame, p == 0 ? new Color(.25f, 0, 0) : new Color(0, 0, .25f), "Wipe alpha endpoint");
                    frame.SavePng(Path.Combine(directory, $"{kind.Value}-{p:0.0}.png"));
                }
                mix.ApplyProgress(.5f);
                redContent.Root.Modulate = Colors.Black;
                using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
                    if (kind == DirectionTransitionId.Dissolve)
                        AssertBackgroundPixel(frame, new Color(0, 0, .125f), "Background source input froze during dissolve");
                blueContent.Root.Modulate = Colors.Black;
                using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
                    AssertBackgroundPixel(frame, Colors.Black, "Background target input froze during transition");
                redContent.Root.Modulate = Colors.White;
                blueContent.Root.Modulate = Colors.White;
                host.ClearBackdropTransition(StageId.Main);
            }
            redContent.Root.Modulate = Colors.Green;
            await red.ReadyAsync(default);
            using (var frozen = frozenRed.Texture.GetImage())
                AssertColor(frozen.GetPixel(32, 32), new Color(.5f, 0, 0, .5f), "Frozen source changed after live content mutation");
            host.PublishBackdrop(StageId.Main, state, red);
            using (var frame = await ReadTransitionFrameAsync(host.ScreenViewport))
                AssertBackgroundPixel(frame, Colors.Black, "Stable output did not reconnect to live content after snapshots.");
        }
        finally
        {
            red.Dispose(); blue.Dispose();
            await host.ShutdownAsync(CancellationToken.None);
            owner.QueueFree();
        }
    }

    private static void AssertBackgroundPixel(Image frame, Color expected, string message)
    {
        var actual = frame.GetPixel(frame.GetWidth() / 2, frame.GetHeight() / 2);
        Assert(Math.Abs(actual.R - expected.R) < .015f && Math.Abs(actual.G - expected.G) < .015f && Math.Abs(actual.B - expected.B) < .015f,
            $"{message} Expected {expected}, got {actual}.");
    }
}
