#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateGpuScreenSnapshotAsync()
    {
        if (DisplayServer.GetName() == "headless" || RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility") return;
        var initialCount = GodotGpuTextureSnapshot.ActiveCount;
        var output = new SubViewport { Size = new Vector2I(96, 64), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(output);
        var owner = new Node(); output.AddChild(owner);
        using var host = new GodotDirectionRenderHost(owner, output.Size) { UseGpuScreenSnapshots = true };
        await host.InitializeAsync(default);
        host.ScreenViewport.TransparentBg = true;
        foreach (var child in host.ScreenCanvas.GetChildren().OfType<CanvasItem>()) child.Hide();
        using var pixels = Image.CreateEmpty(96, 64, false, Image.Format.Rgba8);
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 96; x++)
                pixels.SetPixel(x, y, new Color(x / 95f, y / 63f, (x % 7) / 6f, .25f + .75f * (y % 5) / 4));
        using var texture = ImageTexture.CreateFromImage(pixels);
        var pattern = new TextureRect { Texture = texture, Size = output.Size };
        host.ScreenCanvas.AddChild(pattern);
        GodotVisualInput[] captures = Array.Empty<GodotVisualInput>();
        try
        {
            captures = await Task.WhenAll(host.CaptureScreenTransitionAsync(default), host.CaptureScreenTransitionAsync(default));
            using var oracle = host.ScreenViewport.GetTexture().GetImage();
            var expected = oracle.GetData();
            foreach (var capture in captures)
            {
                Assert(capture.Texture is Texture2Drd && capture.Update == VisualInputUpdate.Frozen && capture.IsPremultiplied,
                    "GPU capture did not publish an independent frozen RD texture.");
                using var copy = capture.Texture.GetImage();
                Assert(expected.SequenceEqual(copy.GetData()), "GPU snapshot changed orientation, color or premultiplied alpha.");
            }
            Assert(((Texture2Drd)captures[0].Texture).TextureRdRid != ((Texture2Drd)captures[1].Texture).TextureRdRid,
                "Concurrent preparations shared mutable snapshot storage.");
            await CompareGpuSnapshotTransitionsAsync(captures[0], oracle);
            pixels.Fill(Colors.Blue); texture.Update(pixels);
            await GodotVisualInput.WaitForFrameAsync(default);
            using (var frozen = captures[0].Texture.GetImage())
                Assert(expected.SequenceEqual(frozen.GetData()), "Live Screen updates mutated the frozen GPU snapshot.");
            captures[1].Dispose(); captures[1].Dispose();

            var resizing = host.CaptureScreenTransitionAsync(default);
            output.Size = new Vector2I(128, 96);
            using (var resized = await resizing)
                Assert(resized.Bounds.Size == host.ScreenViewport.Size && resized.Bounds.Size != captures[0].Bounds.Size,
                    "Capture after resize retained the old extent.");
            using (var frozen = captures[0].Texture.GetImage())
                Assert(expected.SequenceEqual(frozen.GetData()), "Resize invalidated an earlier GPU snapshot.");

            using (var cancelled = new CancellationTokenSource())
            {
                var pending = host.CaptureScreenTransitionAsync(cancelled.Token);
                cancelled.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(async () => { using var ignored = await pending; });
            }
            var resetPending = host.CaptureScreenTransitionAsync(default);
            await host.ResetAsync(default);
            await AssertThrowsAsync<OperationCanceledException>(async () => { using var ignored = await resetPending; });

            // Exercise the actual prepared component's ownership, not only the copy helper.
            var before = DirectionState.CreateDefault();
            var plan = new DirectionPlanner().Plan(before, new IDirectionDirective[]
            {
                new ScreenPatch(new LayoutId("split"), new Dictionary<LayoutSlotId, ViewId>
                { [LayoutSlotId.Main] = ViewId.Main, [new LayoutSlotId("inset")] = ViewId.Main },
                    new DirectionTransitionSpec(DirectionTransitionId.Dissolve, 10)),
            }).DomainPlan;
            var backend = new GodotDirectionScreenBackend(host);
            var commit = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);
            await using (var prepared = await backend.PrepareAsync(plan, before.Screen, plan.TargetState.Screen, default))
            {
                prepared.Commit(commit);
                Assert(host.ScreenTransitionSource.Texture is Texture2Drd, "Screen transition did not consume the GPU snapshot.");
                await prepared.FinishAsync(default);
                Assert(host.ScreenTransitionSource.Texture is null, "Finished transition retained its GPU texture consumer.");
            }
            await using (var prepared = await backend.PrepareAsync(plan, before.Screen, plan.TargetState.Screen, default))
            {
                prepared.Commit(commit);
                prepared.Rollback(commit);
                Assert(host.ScreenTransitionSource.Texture is null, "Rollback retained its GPU texture consumer.");
            }
            await using (var abandoned = await backend.PrepareAsync(plan, before.Screen, plan.TargetState.Screen, default)) { }
            var shutdownPending = host.CaptureScreenTransitionAsync(default);
            await host.ShutdownAsync(default);
            await AssertThrowsAsync<OperationCanceledException>(async () => { using var ignored = await shutdownPending; });
            using (var frozen = captures[0].Texture.GetImage())
                Assert(expected.SequenceEqual(frozen.GetData()), "Host shutdown invalidated a separately owned GPU snapshot.");
        }
        finally
        {
            foreach (var capture in captures) capture.Dispose();
            await host.ShutdownAsync(default);
            output.QueueFree();
        }
        await GodotVisualInput.WaitForFrameAsync(default);
        Assert(GodotGpuTextureSnapshot.ActiveCount == initialCount, "GPU Screen snapshot texture leaked.");
        GD.Print("GPU Screen snapshot pixels and lifetime passed.");
    }

    private async Task CompareGpuSnapshotTransitionsAsync(GodotVisualInput gpu, Image oracle)
    {
        var evidence = ProjectSettings.GlobalizePath("res://build/trial00-gpu-snapshot");
        System.IO.Directory.CreateDirectory(evidence);
        var viewport = new SubViewport { Size = oracle.GetSize(), TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(viewport);
        using var outputMaterial = GodotDirectionRendering.CreateOutputMaterial();
        var surface = new TextureRect { Size = viewport.Size, Material = outputMaterial };
        viewport.AddChild(surface);
        using var cpu = ImageTexture.CreateFromImage(oracle);
        using var target = SolidTexture(new Color(.1f, .6f, .9f, .7f));
        try
        {
            foreach (var driver in new ITransitionDriver[] { new DissolveTransitionDriver(), new FadeTransitionDriver() })
            {
                foreach (var progress in new[] { 0f, .25f, .5f, .75f, 1f })
                {
                    byte[]? expected = null;
                    foreach (var source in new Texture2D[] { cpu, gpu.Texture })
                    {
                        surface.Texture = source;
                        surface.Modulate = Colors.White;
                        using var effect = GodotVisualTransition.Prepare(driver,
                            new VisualTransitionContext(surface, new DirectionTransitionSpec(driver.Id, 1)),
                            GodotVisualInput.Frozen(source, gpu.Bounds),
                            GodotVisualInput.Static(target, new Rect2(Vector2.Zero, target.GetSize())));
                        effect.ApplyProgress(progress);
                        await GodotVisualInput.WaitForFrameAsync(default);
                        await GodotVisualInput.WaitForFrameAsync(default);
                        using var image = viewport.GetTexture().GetImage();
                        image.SavePng(System.IO.Path.Combine(evidence, $"{driver.Id.Value}-{progress}-{(source == cpu ? "cpu" : "gpu")}.png"));
                        if (expected is null) expected = image.GetData();
                        else Assert(expected.SequenceEqual(image.GetData()),
                            $"GPU and CPU snapshot {driver.Id} outputs differ at progress {progress}.");
                    }
                }
            }
        }
        finally
        {
            surface.Texture = null;
            surface.Material = null;
            viewport.QueueFree();
        }
    }
}
