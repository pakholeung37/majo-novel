#nullable enable

using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Xunit;

namespace Majo.Runtime.Domain.UnitTests;

public sealed class DirectionRuntimeTests
{

    [Fact]
    public void CurtainPatchIsDurableAndUsesEffectTransition()
    {
        var planner = new DirectionPlanner();
        var plan = planner.Plan(DirectionState.CreateDefault(), new IDirectionDirective[]
        {
            new CurtainPatch(
                new CurtainState(new DirectionColor(0.1f, 0.2f, 0.3f), 1),
                new DirectionTransitionSpec(DirectionTransitionId.Fade, 0.5)),
        });

        Assert.Equal(1, plan.TargetState.Effects.Curtain.Opacity);
        var transition = Assert.Single(plan.Transitions);
        Assert.Contains("effect/curtain", transition.StablePaths);
        Assert.Equal(DirectionTransitionId.Fade, transition.Spec.Kind);
        Assert.Empty(plan.Cues);
    }

    [Fact]
    public void PlannerReportsBothLocationsForConflictingComponentTransitions()
    {
        var planner = new DirectionPlanner();
        var first = new DirectionSourceLocation("Opening", 4, 0);
        var second = new DirectionSourceLocation("Opening", 5, 0);

        var exception = Assert.Throws<DirectionConflictException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                View(ViewId.Main, zoom: 1.5f,
                    transition: new DirectionTransitionSpec(DirectionTransitionId.Move, 1),
                    location: first),
                View(ViewId.Main, rotation: 10,
                    transition: new DirectionTransitionSpec(DirectionTransitionId.Fade, 1),
                    location: second),
            }));

        Assert.Contains("Opening:4#0", exception.Message);
        Assert.Contains("Opening:5#0", exception.Message);
    }

    [Fact]
    public void PlannerRejectsCutAndMoveInOnePoseSlot()
    {
        var error = Assert.Throws<DirectionConflictException>(() => new DirectionPlanner().Plan(
            DirectionState.CreateDefault(), new IDirectionDirective[]
            {
                View(ViewId.Main, center: new DirectionPoint(30, 50)),
                View(ViewId.Main, zoom: 2, transition: new DirectionTransitionSpec(DirectionTransitionId.Move, .4)),
            }));
        Assert.Equal("view/main/pose", error.StablePath);
    }

    [Fact]
    public void StageReleaseRemovesItsDefaultAndAdditionalViews()
    {
        var planner = new DirectionPlanner();
        var illustration = new StageId("illustration");
        var illustrationView = new ViewId("illustration");
        var closeup = new ViewId("closeup");
        var created = planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new StageCreatePatch(
                    illustration,
                    illustrationView,
                    DirectionTransitionSpec.Cut),
                new ViewCreatePatch(
                    closeup,
                    illustration,
                    DirectionTransitionSpec.Cut),
            }).TargetState;

        var released = planner.Plan(
            created,
            new IDirectionDirective[]
            {
                new StageReleasePatch(illustration, DirectionTransitionSpec.Cut),
            }).TargetState;

        Assert.DoesNotContain(illustration, released.Stages.Keys);
        Assert.DoesNotContain(illustrationView, released.Views.Keys);
        Assert.DoesNotContain(closeup, released.Views.Keys);
    }

    private static ViewPosePatch View(
        ViewId viewId,
        DirectionPoint? center = null,
        float? zoom = null,
        float? rotation = null,
        DirectionTransitionSpec? transition = null,
        DirectionSourceLocation? location = null) => new(
            viewId,
            center is { } centerValue
                ? DirectionValue.From(centerValue)
                : default,
            zoom is { } zoomValue
                ? DirectionValue.From(zoomValue)
                : default,
            rotation is { } rotationValue
                ? DirectionValue.From(rotationValue)
                : default,
            transition ?? DirectionTransitionSpec.Cut,
            location);
}
