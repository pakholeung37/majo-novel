using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Xunit;

namespace Majo.Runtime.Domain.UnitTests;

public sealed class DirectionCommitSamplingTests
{
    [Fact]
    public void SamplesAreConsumedWithoutMutatingTheImmutablePlanningTarget()
    {
        var actorId = new ActorId("hero");
        var plan = new DirectionPlanner().PlanDomain(DirectionState.CreateDefault(),
            [ActorPatch.Place(StageId.Main, actorId)]);
        var target = plan.TargetState;
        var sampledActor = target.Stages[StageId.Main].Actors[actorId] with
        { Position = new(72, 15), Appearance = "Smile" };
        var sampledView = target.Views[ViewId.Main] with
        { Pose = ViewPose.Default with { Center = new(62, 70) } };
        plan.RecordActorCommit(StageId.Main, sampledActor);
        plan.RecordViewCommit(sampledView);
        Assert.True(plan.TryGetActorCommit(StageId.Main, actorId, out var staged));
        Assert.Equal(sampledActor, staged);

        var committed = plan.ResolveCommitState();

        Assert.Equal(sampledActor, committed.Stages[StageId.Main].Actors[actorId]);
        Assert.Equal(sampledView, committed.Views[ViewId.Main]);
        Assert.Equal(target.Revision, committed.Revision);
        Assert.Equal(target.Scene, committed.Scene);
        Assert.NotEqual(sampledActor, target.Stages[StageId.Main].Actors[actorId]);
        Assert.NotEqual(sampledView, target.Views[ViewId.Main]);
        Assert.False(plan.TryGetActorCommit(StageId.Main, actorId, out _));
        Assert.Same(target, plan.ResolveCommitState());
    }

    [Fact]
    public void ActorsWithTheSameIdOnDifferentStagesRetainIndependentCommitSamples()
    {
        var actorId = new ActorId("hero");
        var other = new StageId("other");
        var plan = new DirectionPlanner().PlanDomain(DirectionState.CreateDefault(),
        [
            new StageCreatePatch(other, new ViewId("other"), DirectionTransitionSpec.Cut),
            ActorPatch.Place(StageId.Main, actorId),
            ActorPatch.Place(other, actorId),
        ]);
        var first = plan.TargetState.Stages[StageId.Main].Actors[actorId] with { Position = new(10, 20) };
        var second = plan.TargetState.Stages[other].Actors[actorId] with { Position = new(80, 40) };
        plan.RecordActorCommit(StageId.Main, first);
        plan.RecordActorCommit(other, second);

        var committed = plan.ResolveCommitState();

        Assert.Equal(first, committed.Stages[StageId.Main].Actors[actorId]);
        Assert.Equal(second, committed.Stages[other].Actors[actorId]);
    }
}
