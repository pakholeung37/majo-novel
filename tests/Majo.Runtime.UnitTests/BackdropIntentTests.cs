using Majo.Runtime.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class BackdropIntentTests
{
    internal static ActorDefinitionResolver CreateResolver(params string[] ids) => new(
        ids.Select(id => new ActorDefinition(id, "background")), [new BackgroundMetadata()]);

    private static BackdropIntent Intent(string id, string? appearance = null, float? distance = null) =>
        new(StageId.Main, id, appearance, distance is { } value ? DirectionValue.From(value) : default,
            null, DirectionTransitionSpec.Cut);

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
    public void MissingDefinitionsUseConventionsButWrongDomainsFailBeforePreparation()
    {
        Assert.Equal("missing", new DirectionPlanner(CreateResolver("Court"))
            .PlanDomain(DirectionState.CreateDefault(), [Intent("missing")]).TargetState.Stages[StageId.Main].Backdrop!.DefinitionId);
        var excluded = new ActorDefinitionResolver([], [new BackgroundMetadata()],
            unsupportedDefinitions: [new("Court", "background")]);
        Assert.Throws<ActorDefinitionException>(() => new DirectionPlanner(excluded)
            .PlanDomain(DirectionState.CreateDefault(), [Intent("Court")]));
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

    private sealed class BackgroundMetadata : IActorFactory
    {
        public string FactoryId => "background";
        public string GetInitialAppearance(ActorDefinition definition) => "Day";
        public bool IsIncrementalAppearance(string appearance) => appearance.StartsWith('+');
        public string ResolveAppearance(ActorDefinition definition, string sourceAppearance, string appearance) =>
            IsIncrementalAppearance(appearance) ? sourceAppearance + appearance : appearance;
        public Task<IActor> CreateAsync(ActorDefinition definition, ActorCreationMode mode, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Planning must not create actors.");
    }
}
