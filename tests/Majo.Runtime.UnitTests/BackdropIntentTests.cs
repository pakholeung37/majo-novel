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
    public void MissingDefinitionsUseConventionsButWrongDomainsFailBeforePreparation()
    {
        Assert.Equal("missing", new DirectionPlanner(CreateResolver("Court"))
            .PlanDomain(DirectionState.CreateDefault(), [Intent("missing")]).TargetState.Stages[StageId.Main].Backdrop!.DefinitionId);
        var excluded = new ActorDefinitionResolver([], [new BackgroundMetadata()],
            unsupportedDefinitions: [new("Court", "background")]);
        Assert.Throws<ActorDefinitionException>(() => new DirectionPlanner(excluded)
            .PlanDomain(DirectionState.CreateDefault(), [Intent("Court")]));
    }

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
