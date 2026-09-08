using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.HeadlessTests;

public partial class HeadlessTestRunner
{
    private async Task ValidateSpatialBackdropAsync()
    {
        var owner = new Node { Name = "SpatialBackdropProbe" };
        AddChild(owner);
        var host = new GodotDirectionRenderHost(owner, new Vector2I(640, 360));
        await host.InitializeAsync(CancellationToken.None);
        using var pixels = Image.CreateEmpty(16, 12, false, Image.Format.Rgba8);
        pixels.Fill(Colors.Red);
        using var texture = ImageTexture.CreateFromImage(pixels);
        var state = new BackdropState(new ResourceKey("generated"))
        {
            Placement = new BackdropPlacement(new DirectionPoint(250, 50), new DirectionPoint(.5f, .5f), 2),
        };
        host.PublishBackdrop(StageId.Main, state, texture);
        Assert(host.Backdrop.Position.IsEqualApprox(new Vector2(1584, 168)), "Backdrop must preserve world placement beyond the reference frame.");
        Assert(host.Backdrop.Size.IsEqualApprox(new Vector2(32, 24)), "Spatial backdrop must use native texture size times scale.");
        var far = new ViewId("far");
        host.PublishView(far, new ViewState(far, StageId.Main, new ViewPose(new DirectionPoint(250, 50), 1, 0)), texture);
        var view = host.GetOrCreateView(far);
        Assert(view.ContentViewport.World2D == host.StageViewport.World2D, "Views must observe the same Stage world rather than a clipped texture.");
        Assert((view.ContentViewport.CanvasTransform * new Vector2(1600, 180)).IsEqualApprox(new Vector2(320, 180)), "Far content must map to the view center.");
        var other = host.GetOrCreateStage(new StageId("other"));
        Assert(other.Viewport.World2D != view.ContentViewport.World2D, "Separate stages must retain separate worlds.");
        var bounds = GodotDirectionRenderHost.BackdropBounds(host.RequireStage(StageId.Main), state, texture);
        var input = host.PrepareBackdropInput(host.RequireStage(StageId.Main), state, texture, bounds);
        Assert(input.Size == new Vector2I(32, 24), "Backdrop transitions must capture the spatial bounds, not crop to the reference viewport.");
        input.QueueFree();
        var splitState = new ScreenState(new LayoutId("split"),
            new System.Collections.Generic.Dictionary<LayoutSlotId, ViewId>
            { [LayoutSlotId.Main] = ViewId.Main, [new LayoutSlotId("inset")] = far })
        { Split = new SplitLayout(35, 10, new DirectionPoint(20, 50), new DirectionPoint(55, 60)) };
        host.PublishScreen(splitState);
        var leftOutput = host.ScreenCanvas.GetNode<TextureRect>("ScreenSlot-main");
        var rightOutput = host.ScreenCanvas.GetNode<TextureRect>("ScreenSlot-inset");
        var leftMask = (ShaderMaterial)leftOutput.Material;
        var rightMask = (ShaderMaterial)rightOutput.Material;
        Assert(((Vector2)leftMask.GetShaderParameter("anchor")).IsEqualApprox(new Vector2(.2f, .5f)), "Main placement must use screen coordinates.");
        Assert(((Vector2)rightMask.GetShaderParameter("anchor")).IsEqualApprox(new Vector2(.55f, .4f)), "Inset Y must convert from up to down once.");
        Assert(Mathf.IsEqualApprox((float)leftMask.GetShaderParameter("angle"), Mathf.DegToRad(10)), "Mask angle must use physical screen space.");
        Assert(leftMask.GetShaderParameter("divider").Equals(rightMask.GetShaderParameter("divider")), "Both halves must use the same split boundary.");
        Assert(!(bool)leftMask.GetShaderParameter("right_side") && (bool)rightMask.GetShaderParameter("right_side"), "Masks must be complementary.");
        Assert(leftOutput.Position == Vector2.Zero && rightOutput.Position == Vector2.Zero, "Split masks must cover the screen without resizing the camera image to the slot.");
        host.PublishScreenLayout(splitState with { Split = new SplitLayout(65, -10) }, splitState, .5f);
        Assert(Mathf.IsEqualApprox((float)leftMask.GetShaderParameter("divider"), .5f), "Moving layouts must interpolate split placement.");
        Assert(Mathf.IsEqualApprox((float)leftMask.GetShaderParameter("angle"), 0), "Moving layouts must interpolate the boundary angle.");
        host.PublishScreenLayout(ScreenState.Default, splitState, .5f);
        Assert(leftMask.GetShaderParameter("divider").Equals(rightMask.GetShaderParameter("divider")), "Split-to-single motion must retain a shared boundary.");
        host.PublishScreen(splitState with { Split = null });
        Assert(((Vector2)leftMask.GetShaderParameter("anchor")).IsEqualApprox(new Vector2(.25f, .5f)), "Legacy split must retain quarter-screen framing.");
        Assert(((Vector2)rightMask.GetShaderParameter("anchor")).IsEqualApprox(new Vector2(.75f, .5f)), "Legacy inset must retain three-quarter-screen framing.");
        host.PublishScreen(ScreenState.Default);
        Assert(Mathf.IsEqualApprox((float)leftMask.GetShaderParameter("divider"), 1), "Returning to single must remove the split mask.");
        if (DisplayServer.GetName() != "headless")
        {
            using var blue = SolidTexture(Colors.Blue);
            foreach (var angle in new[] { -25f, 10f, 35f })
            {
                host.PublishScreen(splitState with { Split = new SplitLayout(50, angle) });
                leftOutput.Texture = texture;
                rightOutput.Texture = blue;
                using var rendered = await ReadTransitionFrameAsync(host.ScreenViewport);
                var mixed = 0;
                for (var y = 1; y < rendered.GetHeight() - 1; y++)
                {
                    var boundary = rendered.GetWidth() * .5f -
                        (y + .5f - rendered.GetHeight() * .5f) * Mathf.Tan(Mathf.DegToRad(angle));
                    for (var x = Mathf.FloorToInt(boundary) - 2; x <= Mathf.CeilToInt(boundary) + 2; x++)
                    {
                        var pixel = rendered.GetPixel(x, y);
                        Assert(pixel.R + pixel.B > .95f,
                            "Antialiased split must not expose a dark clear-color seam.");
                        if (pixel.R > .02f && pixel.B > .02f) mixed++;
                    }
                }
                Assert(mixed > rendered.GetHeight() / 4,
                    "Diagonal split must contain fractional pixel coverage, not a hard staircase.");
            }
        }
        await host.ShutdownAsync(CancellationToken.None);
        owner.QueueFree();
    }
}
