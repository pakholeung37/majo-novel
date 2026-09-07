#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Resources;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionPatchTests
{
    [Theory]
    [InlineData(DirectionEase.BackIn, DirectionEaseCurve.Back, DirectionEaseMode.In)]
    [InlineData(DirectionEase.BackOut, DirectionEaseCurve.Back, DirectionEaseMode.Out)]
    [InlineData(DirectionEase.BackInOut, DirectionEaseCurve.Back, DirectionEaseMode.InOut)]
    [InlineData(DirectionEase.CubicOut, DirectionEaseCurve.Cubic, DirectionEaseMode.Out)]
    public void DirectionEaseDecomposesIntoCurveAndMode(
        DirectionEase ease,
        DirectionEaseCurve curve,
        DirectionEaseMode mode)
    {
        Assert.Equal(new DirectionEaseParts(curve, mode), ease.Decompose());
    }

    [Fact]
    public void BeatBeginAllowsAnonymousBeats()
    {
        var command = new BeatBeginCommand(
            new FakeDirectionBeatRuntime(),
            new DelegateDialogueNodeAccessor(() => "node"));
        var bound = TestCatalogBuilder.CreateBound(command);
        var arguments = bound.Bind(new CommandInput("beat begin", Array.Empty<string>()));

        Assert.False(arguments.Id.WasProvided);
    }

    [Fact]
    public void DirectionEaseAcceptsLegacyBackCurves()
    {
        var easings = new Majo.Runtime.Timing.EasingRegistry();
        Majo.Runtime.Timing.BuiltInEasings.RegisterDefaults(easings);
        var ease = new DirectionEaseConverter(easings).Parse("back-out");

        Assert.Equal(DirectionEase.BackOut, ease);
    }

    [Fact]
    public void ActorAddressDistinguishesTheSameActorAcrossStages()
    {
        var actor = new ActorId("Ema");

        Assert.NotEqual(
            new ActorAddress(new StageId("cell"), actor),
            new ActorAddress(new StageId("broadcast"), actor));
    }

    [Fact]
    public void DefaultStateProvidesMainStageViewAndScreen()
    {
        var state = DirectionState.CreateDefault();

        Assert.Contains(StageId.Main, state.Stages.Keys);
        Assert.Equal(StageId.Main, state.Views[ViewId.Main].StageId);
        Assert.Equal(ViewId.Main, state.Screen.Bindings[LayoutSlotId.Main]);
        Assert.Equal(0, state.Revision);
    }

    [Fact]
    public void PlannerBuildsCompleteTargetStateAndResolvesSameBeatDefinitions()
    {
        var planner = new DirectionPlanner();
        var cell = new StageId("cell");
        var reaction = new ViewId("reaction");
        var ema = new ActorId("Ema");

        var plan = planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new StageCreatePatch(cell, new ViewId(cell.Value), DirectionTransitionSpec.Cut),
                new ViewCreatePatch(reaction, cell, DirectionTransitionSpec.Cut),
                View(reaction, zoom: 1.8f),
                Actor(ema, cell, position: new DirectionPoint(70, 0)),
                Actor(ema, cell, appearance: "Fearful1,ArmR1"),
                new BackdropPatch(
                    cell,
                    new BackdropState(new ResourceKey("Background_025_001")),
                    DirectionTransitionSpec.Cut),
                new ScreenPatch(
                    new LayoutId("split"),
                    new Dictionary<LayoutSlotId, ViewId>
                    {
                        [new LayoutSlotId("main")] = ViewId.Main,
                        [new LayoutSlotId("inset")] = reaction,
                    },
                    DirectionTransitionSpec.Cut),
            });

        var stage = plan.TargetState.Stages[cell];
        Assert.Equal("Background_025_001", stage.Backdrop?.Resource.ToString());
        Assert.Equal(new DirectionPoint(70, 0), stage.Actors[ema].Position);
        Assert.Equal("Fearful1,ArmR1", stage.Actors[ema].Appearance);
        Assert.Equal(cell, plan.TargetState.Views[reaction].StageId);
        Assert.Equal(1.8f, plan.TargetState.Views[reaction].Pose.Zoom);
        Assert.Equal(reaction, plan.TargetState.Screen.Bindings[new LayoutSlotId("inset")]);
        Assert.Equal(1, plan.TargetState.Revision);
        Assert.Contains(plan.ResourceClaims, claim =>
            claim.Kind == DirectionResourceKind.Backdrop &&
            claim.SemanticId == "Background_025_001");
        Assert.Contains(plan.ResourceClaims, claim =>
            claim.Kind == DirectionResourceKind.ActorAppearance &&
            claim.SemanticId == "Ema/Fearful1,ArmR1");
    }

    [Fact]
    public void PlannerRejectsTwoWritesToTheSameStablePathWithLocations()
    {
        var planner = new DirectionPlanner();
        var first = new DirectionSourceLocation("Trial", 12, 1);
        var second = new DirectionSourceLocation("Trial", 13, 2);

        var exception = Assert.Throws<DirectionConflictException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                View(ViewId.Main, zoom: 1.5f, location: first),
                View(ViewId.Main, zoom: 2, location: second),
            }));

        Assert.Equal("view/main/zoom", exception.StablePath);
        Assert.Equal(first, exception.First);
        Assert.Equal(second, exception.Second);
        Assert.Contains("Trial:12#1", exception.Message);
        Assert.Contains("Trial:13#2", exception.Message);
    }

    [Fact]
    public void PlannerRejectsUnknownStageAndViewReferences()
    {
        var planner = new DirectionPlanner();

        Assert.Throws<DirectionReferenceException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new ViewCreatePatch(
                    new ViewId("missing-view"),
                    new StageId("missing"),
                    DirectionTransitionSpec.Cut),
            }));

        Assert.Throws<DirectionReferenceException>(() => planner.Plan(
            DirectionState.CreateDefault(),
            new IDirectionDirective[]
            {
                new ScreenPatch(
                    LayoutId.SingleLayout,
                    new Dictionary<LayoutSlotId, ViewId>
                    {
                        [LayoutSlotId.Main] = new ViewId("missing"),
                    },
                    DirectionTransitionSpec.Cut),
            }));
    }

    [Fact]
    public void IllustrationStageSwitchesScreenWithoutMutatingMainStage()
    {
        var before = DirectionState.CreateDefault();
        var planner = new DirectionPlanner();
        var illustration = new StageId("illustration");
        var illustrationView = new ViewId("illustration");

        var plan = planner.Plan(before, new IDirectionDirective[]
        {
            new StageCreatePatch(
                illustration,
                illustrationView,
                DirectionTransitionSpec.Cut),
            new BackdropPatch(
                illustration,
                new BackdropState(new ResourceKey("Still_490_001")),
                DirectionTransitionSpec.Cut),
            View(illustrationView, zoom: 3),
            new ScreenPatch(
                LayoutId.SingleLayout,
                new Dictionary<LayoutSlotId, ViewId>
                {
                    [LayoutSlotId.Main] = illustrationView,
                },
                DirectionTransitionSpec.Cut),
        });

        Assert.Same(before.Stages[StageId.Main], plan.TargetState.Stages[StageId.Main]);
        Assert.Equal("Still_490_001", plan.TargetState.Stages[illustration].Backdrop?.Resource.ToString());
        Assert.Equal(illustration, plan.TargetState.Views[illustrationView].StageId);
        Assert.Equal(3, plan.TargetState.Views[illustrationView].Pose.Zoom);
        Assert.Equal(illustrationView, plan.TargetState.Screen.Bindings[LayoutSlotId.Main]);
    }

    [Fact]
    public void PureDomainPlanningWorksWithoutRuntimeAndDecoupledFromExecution()
    {
        var planner = new DirectionPlanner();
        var before = DirectionState.CreateDefault();
        var directives = new IDirectionDirective[]
        {
            View(ViewId.Main, zoom: 2.0f),
        };

        DirectionPlan domainPlan = planner.PlanDomain(before, directives);
        Assert.NotNull(domainPlan);
        Assert.Equal(2.0f, domainPlan.TargetState.Views[ViewId.Main].Pose.Zoom);
        Assert.Single(domainPlan.Transitions);

        DirectionExecutionPlan executionPlan = planner.Plan(before, directives);
        Assert.NotNull(executionPlan);
        Assert.Equal(domainPlan.TargetState.Views[ViewId.Main].Pose.Zoom, executionPlan.TargetState.Views[ViewId.Main].Pose.Zoom);
        Assert.Same(executionPlan.DomainPlan.TargetState, executionPlan.TargetState);
    }

    private static ActorPatch Actor(
        ActorId actorId,
        StageId stageId,
        DirectionPoint? position = null,
        string? appearance = null) => new(
            stageId,
            actorId,
            position is { } point
                ? DirectionValue.From(point)
                : default,
            appearance is not null
                ? DirectionValue.From<string?>(appearance)
                : default,
            default,
            default,
            DirectionTransitionSpec.Cut);

    private static ViewPosePatch View(
        ViewId viewId,
        float? zoom = null,
        float? rotation = null,
        DirectionTransitionSpec? transition = null,
        DirectionSourceLocation? location = null) => new(
            viewId,
            default,
            zoom is { } zoomValue
                ? DirectionValue.From(zoomValue)
                : default,
            rotation is { } rotationValue
                ? DirectionValue.From(rotationValue)
                : default,
            transition ?? DirectionTransitionSpec.Cut,
            location);
}
