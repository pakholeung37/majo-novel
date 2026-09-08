using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn.Preloading;
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DirectionApiContractTests
{
    [Theory]
    [InlineData("camera")]
    [InlineData("camera reset")]
    [InlineData("shot use")]
    [InlineData("character")]
    [InlineData("character appearance")]
    [InlineData("object")]
    [InlineData("backdrop")]
    public void StateCommandsRejectMoveAndCutWithPositiveDuration(string command)
    {
        var policy = new DirectionCommandPolicy();
        Assert.Throws<DirectionException>(() => policy.ResolveTransition(command,
            TransitionArguments.Create(via: CommandValue<DirectionTransitionId>.Explicit(DirectionTransitionId.Move)), null!));
        Assert.Throws<ArgumentException>(() => policy.ResolveTransition(command,
            TransitionArguments.Create(via: CommandValue<DirectionTransitionId>.Explicit(DirectionTransitionId.Cut),
                duration: CommandValue<double>.Explicit(.3)), null!));
    }

    [Fact]
    public void BeatDurationPrecedesCharacterDefaultsAndChildWaitIsRejected()
    {
        var beats = new FakeDirectionBeatRuntime();
        var policy = new DirectionCommandPolicy(beats);
        beats.Begin("beat", "node", new DirectionBeatDefaults(.7, null));
        Assert.Equal(.7, policy.ResolveTransition("character", TransitionArguments.Create(), null!).DurationSeconds);
        Assert.Equal(.7, policy.ResolveMotionTransition("object fx", TimingArguments.Create(), DirectionTransitionId.Move, null!).DurationSeconds);
        Assert.Equal(.2, policy.ResolveTransition("character", TransitionArguments.Create(duration: CommandValue<double>.Explicit(.2)), null!).DurationSeconds);
        Assert.Equal(0, policy.ResolveTransition("camera", TransitionArguments.Create(), null!).DurationSeconds);
        Assert.Throws<DirectionBeatException>(() => policy.ResolveTransition("character", TransitionArguments.Create(wait: CommandValue<bool>.Explicit(true)), null!));
    }

    [Fact]
    public void ObjectPreloadSeparatesAssetAndInstanceAndDoesNotTreatFxAsBackdrop()
    {
        var resource = Assert.Single(YarnPreloadPlanner.Resources(new[] { "object", "knife1", "asset:evidence/knife", "appearance:blue" }));
        Assert.Equal(new YarnPreloadReference("object", "knife1", "evidence/knife", "blue"), resource);
        Assert.Empty(YarnPreloadPlanner.Resources(new[] { "backdrop", "fx", "shake" }));
        Assert.Empty(YarnPreloadPlanner.Resources(new[] { "character", "move", "hero", "at:left" }));
    }

    [Fact]
    public void CreationFxIsValidButHiddenFxAndCreationHideAreRejected()
    {
        var planner = new DirectionPlanner();
        var actor = new ActorId("hero");
        var place = ActorPatch.Place(StageId.Main, actor);
        var hide = new ActorHidePatch(StageId.Main, actor, DirectionActorKind.Character, DirectionTransitionSpec.Cut);
        var fx = new ActorFxCue(new ActorFxTarget(StageId.Main, ActorFxSlot.Instance, actor), DirectionActorKind.Character, "shake");
        var created = planner.PlanDomain(DirectionState.CreateDefault(), new IDirectionDirective[] { fx, place });
        Assert.Single(created.Cues);
        Assert.Throws<DirectionConflictException>(() => planner.PlanDomain(DirectionState.CreateDefault(), new IDirectionDirective[] { place, hide }));
        var hidden = planner.PlanDomain(created.TargetState, new[] { hide });
        Assert.Throws<DirectionReferenceException>(() => planner.PlanDomain(hidden.TargetState, new[] { fx }));
    }
}
