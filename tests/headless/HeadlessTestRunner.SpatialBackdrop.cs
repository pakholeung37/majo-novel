using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Transitions;
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
        Assert(host.StageCanvas.VisibilityLayer == GodotDirectionRenderHost.StageVisibilityLayers,
            "The shared Stage parent must remain visible to background, transition, and subject viewports.");
        Assert(host.Actors.VisibilityLayer == GodotDirectionRenderHost.SubjectVisibilityLayer,
            "The actor tree must remain visible to the subject viewport.");
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
        var metrics = host.GetRenderResourceMetrics();
        Assert(metrics.ViewCount == 2 && metrics.RenderTargetCount == 12,
            "Render metrics must account for five targets per layered View plus Stage and Screen targets.");
        Assert(metrics.EstimatedRgba8ColorBytes > 0,
            "Render metrics must expose the minimum RGBA8 color-buffer cost.");
        Assert(view.ContentViewport.World2D == host.StageViewport.World2D, "Views must observe the same Stage world rather than a clipped texture.");
        Assert((view.ContentViewport.CanvasTransform * new Vector2(1600, 180)).IsEqualApprox(new Vector2(320, 180)), "Far content must map to the view center.");
        var sourceState = state with { Distance = 2 };
        var targetState = state with { Distance = 5 };
        host.PublishBackdropTransition(StageId.Main, sourceState, texture);
        host.PublishBackdrop(StageId.Main, targetState, texture);
        var expectedSource = CameraProjectionCalculator.Calculate(
            view.CurrentPose, view.CurrentParallax, view.CurrentIdle, sourceState.Distance,
            new DirectionPoint(view.Viewport.Size.X, view.Viewport.Size.Y));
        var expectedTarget = CameraProjectionCalculator.Calculate(
            view.CurrentPose, view.CurrentParallax, view.CurrentIdle, targetState.Distance,
            new DirectionPoint(view.Viewport.Size.X, view.Viewport.Size.Y));
        Assert(view.TransitionBackgroundViewport.CanvasTransform.IsEqualApprox(new Transform2D(
            new Vector2(expectedSource.Background.M11, expectedSource.Background.M12),
            new Vector2(expectedSource.Background.M21, expectedSource.Background.M22),
            new Vector2(expectedSource.Background.TX, expectedSource.Background.TY))),
            "Backdrop transition source must use its own distance projection.");
        Assert(view.BackgroundViewport.CanvasTransform.IsEqualApprox(new Transform2D(
            new Vector2(expectedTarget.Background.M11, expectedTarget.Background.M12),
            new Vector2(expectedTarget.Background.M21, expectedTarget.Background.M22),
            new Vector2(expectedTarget.Background.TX, expectedTarget.Background.TY))),
            "Backdrop transition target must use its own distance projection.");
        using (var fade = host.PrepareBackdropVisualTransition(
            StageId.Main,
            new DirectionTransitionSpec(DirectionTransitionId.Fade, 1),
            new FadeTransitionDriver()))
        {
            fade.ApplyProgress(.5f);
            Assert(view.BackgroundOutput.Visible && view.TransitionBackgroundOutput.Visible,
                "Fade must composite the independently projected source over the target.");
            Assert(Mathf.IsEqualApprox(view.TransitionBackgroundOutput.Modulate.A, .5f),
                "Fade progress must be applied after source projection.");
        }
        host.ClearBackdropTransition(StageId.Main);
        var other = host.GetOrCreateStage(new StageId("other"));
        Assert(other.Viewport.World2D != view.ContentViewport.World2D, "Separate stages must retain separate worlds.");
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
        await ValidateCameraChannelsAsync(host, texture);
        await host.ShutdownAsync(CancellationToken.None);
        owner.QueueFree();
    }

    private static async Task ValidateCameraChannelsAsync(GodotDirectionRenderHost host, Texture2D texture)
    {
        var initial = DirectionState.CreateDefault();
        host.PublishView(ViewId.Main, initial.Views[ViewId.Main], texture);
        var backend = new GodotDirectionViewBackend(host);
        var planner = new DirectionPlanner();
        var motionPlan = planner.PlanDomain(initial,
        [new ViewPosePatch(ViewId.Main, DirectionValue.From(new DirectionPoint(80, 50)),
            default, default, new DirectionTransitionSpec(DirectionTransitionId.Move, 30))]);
        await using var motion = await backend.PrepareAsync(motionPlan, ViewId.Main,
            initial.Views[ViewId.Main], motionPlan.TargetState.Views[ViewId.Main], CancellationToken.None);
        motion.Commit(default);
        var environmentPlan = planner.PlanDomain(motionPlan.TargetState,
        [new ViewParallaxPatch(ViewId.Main, new ViewParallaxState(.5f, new(50, 50), new(3, 2)),
            DirectionTransitionSpec.Cut)]);
        await using var environment = await backend.PrepareAsync(environmentPlan, ViewId.Main,
            motionPlan.TargetState.Views[ViewId.Main], environmentPlan.TargetState.Views[ViewId.Main], CancellationToken.None);
        environment.Commit(default);
        var surface = host.GetOrCreateView(ViewId.Main);
        Assert(!motion.Settlement.IsCompleted, "Changing parallax must not settle an active camera motion.");
        Assert(surface.CurrentPose.Center.X < 70, "Changing parallax must not jump to the motion destination.");
        await motion.FinishAsync(CancellationToken.None);
        Assert(surface.CurrentParallax.Strength == .5f, "Finishing camera motion must preserve the new parallax state.");

        var combinedPlan = planner.PlanDomain(environmentPlan.TargetState,
        [
            new ViewPosePatch(ViewId.Main, DirectionValue.From(new DirectionPoint(20, 50)), default, default,
                new DirectionTransitionSpec(DirectionTransitionId.Move, 30)),
            new ViewIdlePatch(ViewId.Main, new ViewIdleState(new(.15f, .08f), 12),
                new DirectionTransitionSpec(DirectionTransitionId.Move, 40)),
        ]);
        await using var combined = await backend.PrepareAsync(combinedPlan, ViewId.Main,
            environmentPlan.TargetState.Views[ViewId.Main], combinedPlan.TargetState.Views[ViewId.Main], CancellationToken.None);
        combined.Commit(default);
        Assert(!combined.Settlement.IsCompleted, "Independent timing in one beat must remain active.");
        var cutPlan = planner.PlanDomain(combinedPlan.TargetState,
        [new ViewPosePatch(ViewId.Main, DirectionValue.From(new DirectionPoint(40, 50)), default, default,
            DirectionTransitionSpec.Cut)]);
        await using var cut = await backend.PrepareAsync(cutPlan, ViewId.Main,
            combinedPlan.TargetState.Views[ViewId.Main], cutPlan.TargetState.Views[ViewId.Main], CancellationToken.None);
        cut.Commit(default);
        Assert(!combined.Settlement.IsCompleted, "Replacing pose must leave the idle envelope running.");
        await combined.FinishAsync(CancellationToken.None);
        Assert(surface.CurrentPose.Center.X == 40 && surface.CurrentIdle.IsEnabled,
            "Finishing an older group must not overwrite its successor's pose.");

        host.ApplyViewState(ViewId.Main, new ViewPose(new(250, 50), 2, 8),
            new ViewParallaxState(1, new(50, 50), new(1.5f, 1)), ViewIdleState.Off);
        host.PublishBackdrop(StageId.Main, new BackdropState(new ResourceKey("generated")) { Distance = 6 }, texture);
        var subject = surface.ContentViewport.CanvasTransform;
        var background = surface.BackgroundViewport.CanvasTransform;
        var sourceBackground = surface.TransitionBackgroundViewport.CanvasTransform;
        var center = new Vector2(surface.Viewport.Size.X, surface.Viewport.Size.Y) / 2;
        var basis = new Transform2D(Mathf.DegToRad(7), new Vector2(1.4f, 1.4f), 0, Vector2.Zero);
        var offset = new Vector2(5, -3);
        var screenFx = new Transform2D(basis.X, basis.Y, center - basis * center + offset);
        host.ApplyViewModifierDelta(ViewId.Main, new CameraPoseDelta(offset, 1.4f, 7));
        Assert(surface.ContentViewport.CanvasTransform.IsEqualApprox(screenFx * subject),
            "Camera FX must uniformly transform the subject output.");
        Assert(surface.BackgroundViewport.CanvasTransform.IsEqualApprox(screenFx * background),
            "Camera FX must scale even saturated background compensation uniformly.");
        Assert(surface.TransitionBackgroundViewport.CanvasTransform.IsEqualApprox(screenFx * sourceBackground),
            "Camera FX must uniformly transform both background transition inputs.");
        host.ClearViewModifierDelta(ViewId.Main);
    }
}
