using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class CameraEnvironmentCommandTests
{
    [Theory]
    [InlineData("gentle")]
    [InlineData("off")]
    public async Task IdleUsesDefaultEnvelopeUnlessZeroIsExplicit(string name)
    {
        var beats = new FakeDirectionBeatRuntime();
        var scopes = new DirectionScopeResolver();
        scopes.EnterNode("Court");
        var command = TestCatalogBuilder.CreateBound(new CameraIdleCommand(beats, scopes,
            new DelegateDialogueNodeAccessor(() => "Court"), new DirectionCommandPolicy(beats)));

        await command.ExecuteAsync(new CommandInput("camera idle", [name]), new("default"));
        Assert.Equal(0.5, Assert.IsType<ViewIdlePatch>(beats.LastSubmitted).Transition.DurationSeconds);
        Assert.Equal(DirectionTransitionId.Move, Assert.IsType<ViewIdlePatch>(beats.LastSubmitted).Transition.Kind);
        await command.ExecuteAsync(new CommandInput("camera idle", [name, "duration:0"]), new("cut"));
        Assert.Equal(DirectionTransitionSpec.Cut, Assert.IsType<ViewIdlePatch>(beats.LastSubmitted).Transition);
    }

    [Fact]
    public async Task ParallaxAndIdleDefaultToImmediateNonBlockingPatches()
    {
        var beats = new FakeDirectionBeatRuntime();
        var scopes = new DirectionScopeResolver();
        scopes.EnterNode("Court");
        var nodes = new DelegateDialogueNodeAccessor(() => "Court");
        var policy = new DirectionCommandPolicy(beats);
        var parallax = TestCatalogBuilder.CreateBound(new CameraParallaxCommand(beats, scopes, nodes, policy));
        var idle = TestCatalogBuilder.CreateBound(new CameraIdleCommand(beats, scopes, nodes, policy));

        await parallax.ExecuteAsync(new CommandInput("camera parallax", ["subtle"]), new("parallax"));
        var parallaxPatch = Assert.IsType<ViewParallaxPatch>(beats.LastSubmitted);
        Assert.Equal(0.25f, parallaxPatch.Parallax.Strength);
        Assert.Equal(DirectionTransitionSpec.Cut, parallaxPatch.Transition);
        Assert.Equal(DirectionCompletionBarrier.Commit, beats.LastBarrier);

        await idle.ExecuteAsync(new CommandInput("camera idle", ["gentle", "duration:0.5", "wait:true"]), new("idle"));
        var idlePatch = Assert.IsType<ViewIdlePatch>(beats.LastSubmitted);
        Assert.Equal(12, idlePatch.Idle.PeriodSeconds);
        Assert.Equal(0.5, idlePatch.Transition.DurationSeconds);
        Assert.Equal(DirectionCompletionBarrier.Settle, beats.LastBarrier);
    }

    [Fact]
    public void PresetCatalogRejectsDuplicatesUnknownNamesAndRanges()
    {
        var catalog = new CameraEnvironmentPresetCatalog();
        Assert.Throws<ArgumentException>(() => catalog.AddDolly("back", -0.5f));
        Assert.Throws<DirectionReferenceException>(() => catalog.ResolveDistance("missing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.AddDistance("invalid", 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.AddDolly("invalid", 0.86f));
    }

    [Fact]
    public void PlannerKeepsEnvironmentSlotsIndependentAndDollyAbsolute()
    {
        var plan = new DirectionPlanner().PlanDomain(DirectionState.CreateDefault(),
        [
            new ViewPosePatch(ViewId.Main, default, default, default, new DirectionTransitionSpec(DirectionTransitionId.Move, 8))
                { Dolly = DirectionValue.From(-0.25f) },
            new ViewParallaxPatch(ViewId.Main,
                new ViewParallaxState(0.5f, new(50, 50), new(3, 2)), new DirectionTransitionSpec(DirectionTransitionId.Move, 2)),
            new ViewIdlePatch(ViewId.Main,
                new ViewIdleState(new(0.15f, 0.08f), 12), new DirectionTransitionSpec(DirectionTransitionId.Move, .5)),
        ]);

        var view = plan.TargetState.Views[ViewId.Main];
        Assert.Equal(-0.25f, view.Pose.Dolly);
        Assert.Equal(0.5f, view.Parallax.Strength);
        Assert.True(view.Idle.IsEnabled);
    }
}
