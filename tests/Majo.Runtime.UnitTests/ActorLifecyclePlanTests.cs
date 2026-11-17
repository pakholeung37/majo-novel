using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class ActorLifecyclePlanTests
{
    private static readonly ActorId Hero = new("hero");
    private static ActorPatch Place() => ActorPatch.Place(StageId.Main, Hero);

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
        Assert.Equal(6, snapshot.SchemaVersion);
        await direction.ClearSceneAsync(new DirectionSceneContext("project", "next", 8), _ => Task.CompletedTask);
        await contributor.CommitPreparedAsync(snapshot, default);
        Assert.Equal(actor, direction.State.Stages[StageId.Main].Actors[Hero]);
        Assert.Equal("room", direction.State.Scene.SceneId);
        Assert.True(direction.State.Scene.Epoch > 8);
    }
}
