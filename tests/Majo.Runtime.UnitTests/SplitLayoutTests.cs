using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.State;
using Majo.Runtime.Timing;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class SplitLayoutTests
{
    [Fact]
    public async Task CommandPlacementSurvivesRestoreAndSingleClearsIt()
    {
        var beats = new FakeDirectionBeatRuntime();
        var scopes = new DirectionScopeResolver();
        var command = TestCatalogBuilder.CreateBound(new ScreenSplitCommand(beats, scopes,
            new DelegateDialogueNodeAccessor(() => "Court"), new DirectionCommandPolicy(beats)));
        await command.ExecuteAsync(new CommandInput("screen split", ["left:main", "right:main",
            "divider:35", "angle:10", "leftAt:20,50", "rightAt:55,60"]), new CommandExecutionContext("split"));
        var patch = Assert.IsType<ScreenPatch>(beats.LastSubmitted);
        var expected = new SplitLayout(35, 10, new DirectionPoint(20, 50), new DirectionPoint(55, 60));
        Assert.Equal(expected, patch.Split);
        using var direction = new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true });
        await direction.ExecuteAsync([new ViewPosePatch(ViewId.Main,
            DirectionValue.From(new DirectionPoint(35, 71)), DirectionValue.From(3.6f),
            DirectionValue.From(8f), DirectionTransitionSpec.Cut), patch]);
        var pose = direction.State.Views[ViewId.Main].Pose;
        var contributor = new DirectionStateContributor(direction);
        var saved = await contributor.CaptureAsync(default);
        await direction.ResetAsync(default);
        await contributor.RestoreAsync(saved, default);
        Assert.Equal(expected, direction.State.Screen.Split);
        var show = TestCatalogBuilder.CreateBound(new ScreenShowCommand(beats, scopes,
            new DelegateDialogueNodeAccessor(() => "Court"), new DirectionCommandPolicy(beats)));
        await show.ExecuteAsync(new CommandInput("screen show", ["main"]), new CommandExecutionContext("single"));
        await direction.ExecuteAsync([Assert.IsType<ScreenPatch>(beats.LastSubmitted)]);
        Assert.Null(direction.State.Screen.Split);
        Assert.Equal(pose, direction.State.Views[ViewId.Main].Pose);
        var pip = TestCatalogBuilder.CreateBound(new ScreenPipCommand(beats, scopes,
            new DelegateDialogueNodeAccessor(() => "Court"), new DirectionCommandPolicy(beats)));
        await pip.ExecuteAsync(new CommandInput("screen pip", ["background:main", "inset:main"]),
            new CommandExecutionContext("pip"));
        await direction.ExecuteAsync([Assert.IsType<ScreenPatch>(beats.LastSubmitted)]);
        Assert.Equal("pip", direction.State.Screen.Layout.Value);
        Assert.Null(direction.State.Screen.Split);
        Assert.Equal(pose, direction.State.Views[ViewId.Main].Pose);
    }

    [Fact]
    public async Task ShowingAViewDoesNotFinishItsActiveCameraMotion()
    {
        var backend = new FakeDirectionBackend();
        using var direction = new DirectionTransactionCoordinator(new DirectionPlanner(), backend);
        var motion = await direction.ExecuteAsync([new ViewPosePatch(ViewId.Main,
            DirectionValue.From(new DirectionPoint(0, 56)), DirectionValue.From(1f), default,
            new DirectionTransitionSpec(DirectionTransitionId.Move, 8, EasingId.Linear))]);
        var beats = new DirectionBeatRuntime(direction);
        var show = TestCatalogBuilder.CreateBound(new ScreenShowCommand(beats, new DirectionScopeResolver(),
            new DelegateDialogueNodeAccessor(() => "Court"), new DirectionCommandPolicy(beats)));

        await show.ExecuteAsync(new CommandInput("screen show", ["main", "via:cut"]),
            new CommandExecutionContext("show"));

        Assert.False(motion.Settlement.IsCompleted);
        Assert.Equal(0, backend.FinishCount);
        Assert.Equal(ViewId.Main, direction.State.Screen.Bindings[LayoutSlotId.Main]);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(101, 0)]
    [InlineData(50, 90)]
    [InlineData(50, -90)]
    [InlineData(float.NaN, 0)]
    [InlineData(50, float.PositiveInfinity)]
    public void InvalidGeometryIsRejected(float divider, float angle) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitLayout(divider, angle).Validate());
}
