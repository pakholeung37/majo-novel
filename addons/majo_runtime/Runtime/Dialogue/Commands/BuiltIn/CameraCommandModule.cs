#nullable enable

using System;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CameraCommandModule : IRuntimeCommandModule
{
    public void Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands
            .Register(new CameraMoveCommandHandler())
            .Register(new CameraZoomCommandHandler())
            .Register(new CameraRotateCommandHandler())
            .Register(new CameraPoseCommandHandler())
            .Register(new CameraResetCommandHandler())
            .Register(new CameraShakeCommandHandler());
    }
}

internal static class CameraCommandExecution
{
    public static CameraViewTransitionSpec ViewTransition(
        CameraViewTransitionCommandArguments arguments,
        RuntimeCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        var duration = arguments.Duration ??
            (arguments.Via == CameraViewTransitionKind.Cut
                ? 0
                : context.Binding?.DefaultAnimationDurationSeconds ??
                    new StageOperationSpec().DurationSeconds);
        if (arguments.Via == CameraViewTransitionKind.Cut && duration > 0)
            throw new RuntimeCommandException(
                "camera",
                "camera pose/reset via:cut requires duration:0 or an omitted duration.");
        return new CameraViewTransitionSpec(
            arguments.Via,
            StageCommandExecution.Operation(
                duration,
                arguments.Operation,
                arguments.Group,
                arguments.Curve)).Validate();
    }
}

public abstract class CameraOperationCommandArguments
{
    [CommandParameter("wait", 12)] public bool Wait { get; set; }
    [CommandParameter("group", 13)] public string? Group { get; set; }
    [CommandParameter("operation", 14)] public string? Operation { get; set; }
}

public abstract class CameraMotionCommandArguments : CameraOperationCommandArguments
{
    [CommandParameter("duration", 10, DefaultSource = CommandDefaultSource.AnimationDuration, Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("curve", 11, DefaultValue = "linear")]
    public MotionCurve Curve { get; set; }
}

public abstract class CameraViewTransitionCommandArguments : CameraOperationCommandArguments
{
    [CommandParameter("via", 10, DefaultValue = "cut")]
    public CameraViewTransitionKind Via { get; set; }

    [CommandParameter("duration", 11, Minimum = 0)]
    public double? Duration { get; set; }

    [CommandParameter("curve", 15, DefaultValue = "linear")]
    public MotionCurve Curve { get; set; }
}

[RuntimeCommand("camera", "move", Category = "Camera", Usage = "camera move to:<x,y> [duration:<seconds>] [curve:<curve>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraMoveCommandArguments : CameraMotionCommandArguments
{
    [CommandParameter("to", 0, Required = true, ValueHint = "x,y")]
    public Vector2 To { get; set; }
}

public sealed class CameraMoveCommandHandler : IRuntimeCommandHandler<CameraMoveCommandArguments>
{
    public Task ExecuteAsync(CameraMoveCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).MoveTo(
                a.To,
                StageCommandExecution.Operation(
                    a.Duration, a.Operation, a.Group, a.Curve)),
            a.Wait,
            c);
}

[RuntimeCommand("camera", "zoom", Category = "Camera", Usage = "camera zoom to:<zoom> [duration:<seconds>] [curve:<curve>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraZoomCommandArguments : CameraMotionCommandArguments
{
    [CommandParameter("to", 0, Required = true, Minimum = 0.001)] public double To { get; set; }
}
public sealed class CameraZoomCommandHandler : IRuntimeCommandHandler<CameraZoomCommandArguments>
{
    public Task ExecuteAsync(CameraZoomCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).ZoomTo(
                (float)a.To,
                StageCommandExecution.Operation(a.Duration, a.Operation, a.Group, a.Curve)), a.Wait, c);
}

[RuntimeCommand("camera", "rotate", Category = "Camera", Usage = "camera rotate to:<degrees> [duration:<seconds>] [curve:<curve>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraRotateCommandArguments : CameraMotionCommandArguments
{
    [CommandParameter("to", 0, Required = true)] public double To { get; set; }
}
public sealed class CameraRotateCommandHandler : IRuntimeCommandHandler<CameraRotateCommandArguments>
{
    public Task ExecuteAsync(CameraRotateCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).RotateTo(
                (float)a.To,
                StageCommandExecution.Operation(a.Duration, a.Operation, a.Group, a.Curve)), a.Wait, c);
}

[RuntimeCommand("camera", "pose", Category = "Camera", Usage = "camera pose [center:<x,y>] [zoom:<number>] [rotation:<degrees>] [via:<cut|move|dissolve>] [duration:<seconds>] [curve:<curve>] [wait:<bool>] [group:<id>] [operation:<id>]")]
[CommandParameterSet(CommandParameterSetRule.AtLeastOne, "center", "zoom", "rotation")]
public sealed class CameraPoseCommandArguments : CameraViewTransitionCommandArguments
{
    [CommandParameter("center", 0)] public Vector2? Center { get; set; }
    [CommandParameter("zoom", 1, Minimum = 0.001)] public double? Zoom { get; set; }
    [CommandParameter("rotation", 2)] public double? Rotation { get; set; }
}

public sealed class CameraPoseCommandHandler : IRuntimeCommandHandler<CameraPoseCommandArguments>
{
    public Task ExecuteAsync(CameraPoseCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).TransitionTo(
                new CameraPoseTransitionRequest(
                    new CameraPoseUpdate(
                        a.Center,
                        a.Zoom is null ? null : (float)a.Zoom.Value,
                        a.Rotation is null ? null : (float)a.Rotation.Value),
                    CameraCommandExecution.ViewTransition(a, c))),
            a.Wait,
            c);
}

[RuntimeCommand("camera", "reset", Category = "Camera", Usage = "camera reset [via:<cut|move|dissolve>] [duration:<seconds>] [curve:<curve>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraResetCommandArguments : CameraViewTransitionCommandArguments
{
}
public sealed class CameraResetCommandHandler : IRuntimeCommandHandler<CameraResetCommandArguments>
{
    public Task ExecuteAsync(CameraResetCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).ResetCamera(
                CameraCommandExecution.ViewTransition(a, c)), a.Wait, c);
}

[RuntimeCommand("camera", "shake", Category = "Camera", Usage = "camera shake [strength:<number>] [axis:<all|x|y|diagonal>] [frequency:<number>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraShakeCommandArguments
{
    [CommandParameter("strength", 0, DefaultValue = "8", Minimum = 0)] public double Strength { get; set; }
    [CommandParameter("axis", 1, DefaultValue = "all")] public CameraShakeAxis Axis { get; set; }
    [CommandParameter("frequency", 2, DefaultValue = "20", Minimum = 0.001)] public double Frequency { get; set; }
    [CommandParameter("duration", 3, DefaultValue = "0.4", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 4)] public bool Wait { get; set; }
    [CommandParameter("group", 5)] public string? Group { get; set; }
    [CommandParameter("operation", 6)] public string? Operation { get; set; }
}
public sealed class CameraShakeCommandHandler : IRuntimeCommandHandler<CameraShakeCommandArguments>
{
    public Task ExecuteAsync(CameraShakeCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).Shake(new CameraShakeSpec(
                (float)a.Strength, a.Axis, a.Frequency,
                StageCommandExecution.Operation(a.Duration, a.Operation, a.Group))), a.Wait, c);
}
