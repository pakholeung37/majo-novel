#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Resources;
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
        var registry = new RuntimeCommandRegistry();
        registry.Register<BeatBeginDirectionCommandArguments>((_, _) => Task.CompletedTask);
        var descriptor = Assert.Single(registry.Registrations).Descriptor;
        var arguments = new CommandBinder(new CommandBindingContext(0.35))
            .Bind<BeatBeginDirectionCommandArguments>(descriptor, Array.Empty<string>());

        Assert.Null(arguments.Id);
    }

    [Fact]
    public void DirectionEaseAcceptsLegacyBackCurves()
    {
        var registry = new RuntimeCommandRegistry();
        registry.Register<CameraPoseDirectionCommandArguments>((_, _) => Task.CompletedTask);
        var descriptor = Assert.Single(registry.Registrations).Descriptor;
        var arguments = new CommandBinder(new CommandBindingContext(0.35))
            .Bind<CameraPoseDirectionCommandArguments>(
                descriptor,
                new[] { "center:44.438,71.950", "duration:0.4", "ease:back-out" });

        Assert.Equal("back-out", arguments.Ease);
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
        var view = Assert.IsType<StageViewSource>(state.Views[ViewId.Main].Source);
        Assert.Equal(StageId.Main, view.StageId);
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
                View(reaction, source: new StageViewSource(cell), zoom: 1.8f),
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
        Assert.Equal(cell, Assert.IsType<StageViewSource>(
            plan.TargetState.Views[reaction].Source).StageId);
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
            new[]
            {
                View(ViewId.Main, source: new StageViewSource(new StageId("missing"))),
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
    public void CgChangesViewSourceWithoutMutatingStage()
    {
        var before = DirectionState.CreateDefault();
        var planner = new DirectionPlanner();

        var plan = planner.Plan(before, new[]
        {
            View(
                ViewId.Main,
                source: new CgViewSource(new ResourceKey("Still_490_001")),
                zoom: 3),
        });

        Assert.Same(before.Stages[StageId.Main], plan.TargetState.Stages[StageId.Main]);
        var source = Assert.IsType<CgViewSource>(plan.TargetState.Views[ViewId.Main].Source);
        Assert.Equal("Still_490_001", source.Resource.ToString());
        Assert.Equal(3, plan.TargetState.Views[ViewId.Main].Pose.Zoom);
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

    private static ViewPatch View(
        ViewId viewId,
        ViewSource? source = null,
        float? zoom = null,
        float? rotation = null,
        DirectionTransitionSpec? transition = null,
        DirectionSourceLocation? location = null) => new(
            viewId,
            source is not null
                ? DirectionValue.From(source)
                : default,
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
