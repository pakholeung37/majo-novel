using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Operations;
using Majo.Runtime.Timing;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class StageCommandTests
{
    [Fact]
    public void DirectionCameraCommandsBuildAValidatedCatalog()
    {
        var beats = new FakeDirectionBeatRuntime();
        var scopes = new DirectionScopeResolver();
        var nodeAccessor = new DelegateDialogueNodeAccessor(() => "node");
        var policy = new DirectionCommandPolicy(beats, DirectionAnimationDefaults.FromConfiguration(null));
        var services = new ServiceResolverBuilder()
            .Add<IDirectionTransactionCoordinator>(new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true }))
            .Add<IDirectionBeatRuntime>(beats)
            .Add<IDirectionScopeResolver>(scopes)
            .Add<IDialogueNodeAccessor>(nodeAccessor)
            .Add<IDirectionCommandPolicy>(policy)
            .Build();

        var registry = new RuntimeCommandRegistry();
        registry.RegisterModule(new DirectionCommandModule());
        registry.SealRegistrations();
        var catalog = registry.BuildCatalog(services);

        Assert.Contains(catalog.Commands, command => command.Name == "camera move");
        Assert.Contains(catalog.Commands, command => command.Name == "camera");
        Assert.Contains(catalog.Commands, command => command.Name == "camera reset");
        Assert.Contains(catalog.Commands, command => command.Name == "camera fx");

        var move = catalog.Get<CameraMoveCommand.Arguments>("camera move");
        var moveArgs = move.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["center"] = "12,-4",
            ["ease"] = "back-out",
            ["wait"] = "true"
        }));
        Assert.Equal(12, Assert.IsType<PositionRef.Literal>(moveArgs.Center.Value).Point.X);
        Assert.Equal(-4, Assert.IsType<PositionRef.Literal>(moveArgs.Center.Value).Point.Y);
        Assert.Equal(EasingId.BackOut, moveArgs.Timing.Ease.Value);
        Assert.True(moveArgs.Timing.Wait.Value);

        var pose = catalog.Get<CameraCommand.Arguments>("camera");
        var poseArguments = pose.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["center"] = "80,-35",
            ["zoom"] = "3",
            ["rotation"] = "-10"
        }));
        Assert.Equal(80, Assert.IsType<PositionRef.Literal>(poseArguments.Center.Value).Point.X);
        Assert.Equal(-35, Assert.IsType<PositionRef.Literal>(poseArguments.Center.Value).Point.Y);
        Assert.Equal(3, poseArguments.Zoom.Value);
        Assert.Equal(-10, poseArguments.Rotation.Value);
        Assert.False(poseArguments.Via.WasProvided);
        Assert.False(poseArguments.Timing.Duration.WasProvided);

        var dissolveArguments = pose.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["zoom"] = "1",
            ["via"] = "dissolve",
            ["duration"] = "0.6",
            ["ease"] = "sine-in-out"
        }));
        Assert.Equal(DirectionTransitionId.Dissolve, dissolveArguments.Via.Value);
        Assert.Equal(0.6, dissolveArguments.Timing.Duration.Value);
        Assert.Equal(EasingId.SineInOut, dissolveArguments.Timing.Ease.Value);

        var reset = catalog.Get<CameraResetCommand.Arguments>("camera reset");
        var resetArguments = reset.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["via"] = "dissolve",
            ["duration"] = "0.6",
            ["wait"] = "true"
        }));
        Assert.False(resetArguments.Stage.WasProvided);
        Assert.False(resetArguments.View.WasProvided);
        Assert.Equal(DirectionTransitionId.Dissolve, resetArguments.Transition.Via.Value);
        Assert.Equal(0.6, resetArguments.Transition.Timing.Duration.Value);
        Assert.True(resetArguments.Transition.Wait.Value);

        var resetStage = reset.Bind(new RawCommandArguments(null, new Dictionary<string, string>
        {
            ["stage"] = "main"
        }));
        Assert.Equal("main", resetStage.Stage.Value);
        Assert.False(resetStage.View.WasProvided);

        var resetAmbiguous = Assert.Throws<RuntimeCommandException>(() =>
            reset.Bind(new RawCommandArguments(null, new Dictionary<string, string>
            {
                ["stage"] = "main",
                ["view"] = "main"
            })));
        Assert.Contains("stage", resetAmbiguous.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("view", resetAmbiguous.Message, StringComparison.OrdinalIgnoreCase);

        var fx = catalog.Get<CameraFxCommand.Arguments>("camera fx");
        var fxArguments = fx.Bind(new RawCommandArguments("shake", new Dictionary<string, string>
        {
            ["stage"] = "illustration",
            ["strength"] = "15",
            ["axis"] = "diagonal",
            ["frequency"] = "30",
            ["duration"] = "0.8",
            ["wait"] = "true"
        }));
        Assert.Equal("shake", fxArguments.Effect);
        Assert.Equal("illustration", fxArguments.Stage.Value);
        Assert.Equal(15, fxArguments.Strength.Value);
        Assert.Equal(CameraShakeAxis.Diagonal, fxArguments.Axis.Value);
        Assert.Equal(30, fxArguments.Frequency.Value);
        Assert.Equal(0.8, fxArguments.Duration.Value);
        Assert.True(fxArguments.Wait.Wait.Value);
    }
}
