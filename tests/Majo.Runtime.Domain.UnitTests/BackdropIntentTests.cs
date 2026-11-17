#nullable enable

using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Xunit;

namespace Majo.Runtime.Domain.UnitTests;

public sealed class BackdropIntentTests
{

    [Fact]
    public void SameBeatResolvesAppearanceAndDistanceFromWorkingState()
    {
        var planner = new DirectionPlanner(CreateResolver("Court"));
        var plan = planner.PlanDomain(DirectionState.CreateDefault(),
            [Intent("Court", "Night", 6), Intent("Court", "+Rain")]);
        var background = plan.TargetState.Stages[StageId.Main].Backdrop!;
        Assert.Equal("Night+Rain", background.Appearance);
        Assert.Equal(6, background.Distance);
        Assert.Equal("Court", background.DefinitionId);
        Assert.Equal("background", background.FactoryId);
        Assert.All(plan.Transitions, transition => Assert.Contains("stage/main/backdrop", transition.StablePaths));
    }

    [Fact]
    public void OmittedAppearanceAlwaysResetsToDefaultWhileDistancePersists()
    {
        var planner = new DirectionPlanner(CreateResolver("Court"));
        var plan = planner.PlanDomain(DirectionState.CreateDefault(), [Intent("Court", "Night", 6), Intent("Court")]);
        var background = plan.TargetState.Stages[StageId.Main].Backdrop!;
        Assert.Equal("Day", background.Appearance);
        Assert.Equal(6, background.Distance);
    }

    [Fact]
    public void NewDefinitionResetsDistanceAndIncrementalBaseline()
    {
        var plan = new DirectionPlanner(CreateResolver("Court", "Room")).PlanDomain(DirectionState.CreateDefault(),
            [Intent("Court", "Night", 6), Intent("Room", "+Rain")]);
        Assert.Equal("Day+Rain", plan.TargetState.Stages[StageId.Main].Backdrop!.Appearance);
        Assert.Equal(1, plan.TargetState.Stages[StageId.Main].Backdrop!.Distance);
    }

    [Fact]
    public void PlacementIsNotImplicitlyInherited()
    {
        var first = Intent("Court") with { Placement = new(new(40, 30), new(.5f, .5f), 2) };
        var plan = new DirectionPlanner(CreateResolver("Court")).PlanDomain(DirectionState.CreateDefault(), [first, Intent("Court")]);
        Assert.Null(plan.TargetState.Stages[StageId.Main].Backdrop!.Placement);
    }

    [Fact]
    public void NullPatchClearsBackdrop()
    {
        var planner = new DirectionPlanner(CreateResolver("Court"));
        var before = planner.PlanDomain(DirectionState.CreateDefault(), [Intent("Court")]).TargetState;
        var plan = planner.PlanDomain(before, [new BackdropPatch(StageId.Main, null, DirectionTransitionSpec.Cut)]);
        Assert.Null(plan.TargetState.Stages[StageId.Main].Backdrop);
    }

    [Fact]
    public void ConflictingTransitionsInSameBeatAreRejected()
    {
        var last = Intent("Court", "Night") with { Transition = new("dissolve", 1) };
        Assert.Throws<DirectionBeatException>(() => new DirectionPlanner(CreateResolver("Court"))
            .PlanDomain(DirectionState.CreateDefault(), [Intent("Court"), last]));
    }

    [Theory]
    [InlineData("", "background", "Day")]
    [InlineData("Court", "", "Day")]
    [InlineData("Court", "background", " ")]
    [InlineData("fx", "background", "Day")]
    public void IncompleteOrReservedIdentityCannotEnterStableState(string id, string factory, string appearance) =>
        Assert.ThrowsAny<ArgumentException>(() => new BackdropState(id, factory, appearance).Validate());
    [Fact]
    public void ChangingFactoryForTheSameDefinitionResetsItsAppearanceBaselineAndDistance()
    {
        var factories = new Dictionary<string, string> { ["Court"] = "background" };
        var planner = new DirectionPlanner(new BackgroundMetadata(factories));
        var before = planner.PlanDomain(DirectionState.CreateDefault(), [Intent("Court", "Night", 6)]).TargetState;
        factories["Court"] = "custom";

        var target = planner.PlanDomain(before, [Intent("Court", "+Rain")]).TargetState.Stages[StageId.Main].Backdrop!;

        Assert.Equal("custom", target.FactoryId);
        Assert.Equal("Day+Rain", target.Appearance);
        Assert.Equal(1, target.Distance);
    }

    private static IBackdropAppearanceResolver CreateResolver(params string[] ids) =>
        new BackgroundMetadata(ids.ToDictionary(id => id, _ => "background"));

    private static BackdropIntent Intent(string id, string? appearance = null, float? distance = null) =>
        new(StageId.Main, id, appearance, distance is { } value ? DirectionValue.From(value) : default,
            null, DirectionTransitionSpec.Cut);

    private sealed class BackgroundMetadata(IReadOnlyDictionary<string, string> factories) : IBackdropAppearanceResolver
    {
        public string ResolveFactoryId(string definitionId) => factories.GetValueOrDefault(definitionId, "background");
        public ResolvedBackdropAppearance ResolveAppearance(string definitionId, string? appearance, string? previousAppearance) =>
            new(ResolveFactoryId(definitionId), appearance is null ? "Day"
                : appearance.StartsWith('+') ? (previousAppearance ?? "Day") + appearance : appearance);
    }
}
