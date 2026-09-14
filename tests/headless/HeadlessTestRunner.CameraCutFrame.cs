using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateCameraCutFrameAsync()
    {
        Assert(DisplayServer.GetName() != "headless", "Camera cut regression requires a real renderer.");
        var owner = new Node { Name = "CameraCutFrameProbe" };
        AddChild(owner);
        var host = new GodotDirectionRenderHost(owner, new Vector2I(640, 360));
        await host.InitializeAsync(CancellationToken.None);
        using var pixels = Image.CreateEmpty(640, 360, false, Image.Format.Rgba8);
        pixels.Fill(Colors.Red);
        pixels.FillRect(new Rect2I(320, 0, 320, 360), Colors.Blue);
        using var texture = ImageTexture.CreateFromImage(pixels);
        host.PublishBackdrop(StageId.Main, new BackdropState("cut-probe", "background", "Default"), CreateBackgroundRaster(host, texture));
        var second = new ViewId("trial_b");
        var oldPose = new ViewPose(new DirectionPoint(25, 50), 2, 0);
        var newPose = new ViewPose(new DirectionPoint(75, 50), 2, 0);
        host.PublishView(second, new ViewState(second, StageId.Main, oldPose), texture);
        using (var warm = await ReadTransitionFrameAsync(host.ScreenViewport)) { }
        // Change the hidden camera and bind it in the same transaction, as Trial00 does.
        for (var cut = 0; cut < 6; cut++)
        {
            var id = cut % 2 == 0 ? second : ViewId.Main;
            var expectBlue = cut % 4 < 2;
            var pose = expectBlue ? newPose : oldPose;
            host.PublishView(id, new ViewState(id, StageId.Main, pose), texture);
            host.PublishScreen(new ScreenState(ScreenState.Default.Layout,
                new Dictionary<LayoutSlotId, ViewId> { [LayoutSlotId.Main] = id }));
            AssertViewRendering(host.RequireView(id), true, "Cut target");
            AssertViewRendering(host.RequireView(id == second ? ViewId.Main : second), false, "Hidden cut source");
            for (var frame = 0; frame < 3; frame++)
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = host.ScreenViewport.GetTexture().GetImage();
                var color = image.GetPixel(image.GetWidth() / 2, image.GetHeight() / 2);
                GD.Print($"Camera cut {cut}, frame {frame}: {color}");
                Assert(expectBlue ? color.B > .9f && color.R < .1f : color.R > .9f && color.B < .1f,
                    $"Camera cut {cut} exposed stale content on rendered frame {frame}: {color}.");
            }
        }
        await host.ShutdownAsync(CancellationToken.None);
        owner.QueueFree();
    }
}
