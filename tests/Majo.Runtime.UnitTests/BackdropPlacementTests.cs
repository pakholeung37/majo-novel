using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.State;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class BackdropPlacementTests
{
    [Fact]
    public async Task SpatialPlacementRoundTripsAndAPlainBackdropReturnsToCover()
    {
        var beats = new FakeDirectionBeatRuntime();
        var scopes = new DirectionScopeResolver();
        var command = TestCatalogBuilder.CreateBound(new BackdropCommand(beats, scopes,
            new DelegateDialogueNodeAccessor(() => "Court"), new DirectionCommandPolicy(beats)));
        await command.ExecuteAsync(new CommandInput("backdrop", ["generated", "at:-50,100", "origin:0,0", "scale:2"]),
            new CommandExecutionContext("spatial"));
        var patch = Assert.IsType<BackdropPatch>(beats.LastSubmitted);
        using var direction = new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true });
        await direction.ExecuteAsync([patch]);
        var contributor = new DirectionStateContributor(direction);
        var record = await contributor.CaptureAsync(default);
        await direction.ResetAsync(default);
        await contributor.CommitPreparedAsync(record, default);
        Assert.Equal(new BackdropPlacement(new DirectionPoint(-50, 100), new DirectionPoint(0, 0), 2),
            direction.State.Stages[StageId.Main].Backdrop!.Placement);
        await command.ExecuteAsync(new CommandInput("backdrop", ["room"]), new CommandExecutionContext("cover"));
        await direction.ExecuteAsync([Assert.IsType<BackdropPatch>(beats.LastSubmitted)]);
        Assert.Null(direction.State.Stages[StageId.Main].Backdrop!.Placement);
    }
}
