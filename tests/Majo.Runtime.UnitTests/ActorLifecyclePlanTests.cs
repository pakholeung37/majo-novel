using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ActorLifecyclePlanTests
{
    private static readonly ActorId Hero = new("hero");
    private static readonly DirectionTransitionSpec Move = new(DirectionTransitionId.Move, 1);
    private static ActorPatch Place() => ActorPatch.Place(StageId.Main, Hero);
    private static DirectionState VisibleState() => new DirectionPlanner().PlanDomain(
        DirectionState.CreateDefault(), new[] { Place() }).TargetState;

    [Fact]
    public async Task SnapshotRetainsHiddenObjectFieldsAndRestoresWithFreshEpoch()
    {
        using var direction = new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true });
        await direction.ClearSceneAsync(new DirectionSceneContext("project", "room", 7), _ => Task.CompletedTask);
        await direction.ExecuteAsync(new[] { Place() with
        {
            Kind = DirectionActorKind.SceneObject, DefinitionId = "evidence/knife",
            Position = DirectionValue.From(new DirectionPoint(-10, 130)), Scale = DirectionValue.From(.8f),
            RotationDegrees = DirectionValue.From(35f), Z = DirectionValue.From(99), Appearance = DirectionValue.From<string?>("highlight"),
        } });
        await direction.ExecuteAsync(new[] { new ActorHidePatch(StageId.Main, Hero, DirectionActorKind.SceneObject, DirectionTransitionSpec.Cut) });
        var actor = direction.State.Stages[StageId.Main].Actors[Hero];
        var contributor = new DirectionStateContributor(direction);
        var snapshot = await contributor.CaptureAsync(default);
        Assert.Equal(3, snapshot.SchemaVersion);
        await direction.ClearSceneAsync(new DirectionSceneContext("project", "next", 8), _ => Task.CompletedTask);
        await contributor.CommitPreparedAsync(snapshot, default);
        Assert.Equal(actor, direction.State.Stages[StageId.Main].Actors[Hero]);
        Assert.Equal("room", direction.State.Scene.SceneId);
        Assert.True(direction.State.Scene.Epoch > 8);
    }

    [Fact]
    public void HideRetainsAllFieldsAndShowRestoresVisibility()
    {
        var planner = new DirectionPlanner();
        var initial = planner.PlanDomain(DirectionState.CreateDefault(), new[]
        {
            Place() with { Position = DirectionValue.From(new DirectionPoint(-10, 30)),
                Appearance = DirectionValue.From<string?>("happy"), Scale = DirectionValue.From(2f),
                RotationDegrees = DirectionValue.From(15f), Z = DirectionValue.From(42) },
        }).TargetState;
        var hidden = planner.PlanDomain(initial, new[] { new ActorHidePatch(StageId.Main, Hero,
            DirectionActorKind.Character, DirectionTransitionSpec.Cut) }).TargetState;
        var record = hidden.Stages[StageId.Main].Actors[Hero];
        Assert.False(record.Visible);
        Assert.Equal(initial.Stages[StageId.Main].Actors[Hero] with { Visible = false }, record);
        var shown = planner.PlanDomain(hidden, new[] { Place() }).TargetState;
        Assert.Equal(initial.Stages[StageId.Main].Actors[Hero], shown.Stages[StageId.Main].Actors[Hero]);
        var cleared = planner.PlanDomain(hidden, new[] { new StageClearPatch(StageId.Main, DirectionTransitionSpec.Cut) });
        Assert.Empty(cleared.TargetState.Stages[StageId.Main].Actors);
    }

    [Fact]
    public void ObjectDefinitionIsRequiredAndCannotChangeTypeOrAsset()
    {
        var planner = new DirectionPlanner();
        Assert.Throws<ArgumentException>(() => planner.PlanDomain(DirectionState.CreateDefault(),
            new[] { Place() with { Kind = DirectionActorKind.SceneObject } }));
        var state = planner.PlanDomain(DirectionState.CreateDefault(), new[]
        {
            Place() with { Kind = DirectionActorKind.SceneObject, DefinitionId = "evidence/knife" },
            Place() with { ActorId = new ActorId("knife2"), Kind = DirectionActorKind.SceneObject, DefinitionId = "evidence/knife" },
        }).TargetState;
        Assert.Equal(2, state.Stages[StageId.Main].Actors.Count);
        Assert.Throws<DirectionReferenceException>(() => planner.PlanDomain(state, new[] { Place() }));
        Assert.Throws<DirectionReferenceException>(() => planner.PlanDomain(state, new[]
        { Place() with { Kind = DirectionActorKind.SceneObject, DefinitionId = "other" } }));
    }

    [Fact]
    public void SpaceIsOneSlotButAppearanceMoveAndHideCanShareBeat()
    {
        var planner = new DirectionPlanner();
        var move = new ActorMovePatch(StageId.Main, Hero, DirectionActorKind.Character,
            DirectionValue.From(new DirectionPoint(80, 0)), default, default, Move);
        var state = VisibleState();
        Assert.Throws<DirectionConflictException>(() => planner.PlanDomain(state, new[]
        { move, move with { Position = default, RotationDegrees = DirectionValue.From(20f) } }));
        var plan = planner.PlanDomain(state, new IDirectionDirective[]
        {
            Place() with { Appearance = DirectionValue.From<string?>("happy") },
            move,
            new ActorHidePatch(StageId.Main, Hero, DirectionActorKind.Character, DirectionTransitionSpec.Cut),
        });
        Assert.False(plan.TargetState.Stages[StageId.Main].Actors[Hero].Visible);
        Assert.Equal(3, plan.ActorIntents.Count);
        Assert.Throws<DirectionReferenceException>(() => planner.PlanDomain(DirectionState.CreateDefault(),
            new IDirectionDirective[] { Place(), move }));
        Assert.Throws<DirectionReferenceException>(() => planner.PlanDomain(plan.TargetState, new[] { move }));
    }
}
