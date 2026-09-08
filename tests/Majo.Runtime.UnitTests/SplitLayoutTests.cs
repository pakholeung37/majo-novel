using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.State;
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
        var command = TestCatalogBuilder.CreateBound(new LayoutCommand(beats, scopes,
            new DelegateDialogueNodeAccessor(() => "Court"), new DirectionCommandPolicy(beats)));
        await command.ExecuteAsync(new CommandInput("layout", ["split", "main:main", "inset:main",
            "divider:35", "angle:10", "mainAt:20,50", "insetAt:55,60"]), new CommandExecutionContext("split"));
        var patch = Assert.IsType<ScreenPatch>(beats.LastSubmitted);
        var expected = new SplitLayout(35, 10, new DirectionPoint(20, 50), new DirectionPoint(55, 60));
        Assert.Equal(expected, patch.Split);
        using var direction = new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true });
        await direction.ExecuteAsync([patch]);
        var contributor = new DirectionStateContributor(direction);
        var saved = await contributor.CaptureAsync(default);
        await direction.ResetAsync(default);
        await contributor.RestoreAsync(saved, default);
        Assert.Equal(expected, direction.State.Screen.Split);
        await command.ExecuteAsync(new CommandInput("layout", ["single"]), new CommandExecutionContext("single"));
        await direction.ExecuteAsync([Assert.IsType<ScreenPatch>(beats.LastSubmitted)]);
        Assert.Null(direction.State.Screen.Split);
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
