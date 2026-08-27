#nullable enable

using System;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class StageCommandModule : IRuntimeCommandModule
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
            .Register(new CameraShakeCommandHandler())
            .Register(new StageTransitionCommandHandler())
            .Register(new StageCurtainCommandHandler())
            .Register(new StageOverlayCommandHandler())
            .Register(new StageLetterboxCommandHandler())
            .Register(new StageFlashCommandHandler());
    }
}

internal static class StageCommandExecution
{
    public static StageOperationSpec Operation(
        double duration,
        string? operation,
        string? group,
        MotionCurve curve = MotionCurve.Linear,
        bool skippable = true) =>
        new StageOperationSpec(duration, operation, group, skippable, curve).Validate();

    public static async Task TrackAndWaitAsync(
        Majo.Runtime.Operations.IOperationHandle operation,
        bool wait,
        RuntimeCommandContext context)
    {
        context.Track(operation);
        if (wait) await operation.Completion.WaitAsync(context.CancellationToken);
    }

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
            Operation(
                duration,
                arguments.Operation,
                arguments.Group,
                arguments.Curve)).Validate();
    }

    public static T Require<T>(RuntimeCommandContext context) where T : class =>
        context.Services?.Get<T>() ?? throw new RuntimeCommandException(
            "stage", $"Stage service '{typeof(T).Name}' is not available.");
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
                    StageCommandExecution.ViewTransition(a, c))),
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
                StageCommandExecution.ViewTransition(a, c)), a.Wait, c);
}

[RuntimeCommand("camera", "shake", Category = "Camera", Usage = "camera shake [strength:<number>] [frequency:<number>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraShakeCommandArguments
{
    [CommandParameter("strength", 0, DefaultValue = "8", Minimum = 0)] public double Strength { get; set; }
    [CommandParameter("frequency", 1, DefaultValue = "20", Minimum = 0.001)] public double Frequency { get; set; }
    [CommandParameter("duration", 2, DefaultValue = "0.4", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 3)] public bool Wait { get; set; }
    [CommandParameter("group", 4)] public string? Group { get; set; }
    [CommandParameter("operation", 5)] public string? Operation { get; set; }
}
public sealed class CameraShakeCommandHandler : IRuntimeCommandHandler<CameraShakeCommandArguments>
{
    public Task ExecuteAsync(CameraShakeCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).Shake(new CameraShakeSpec(
                new Vector2((float)a.Strength, (float)a.Strength), a.Frequency,
                StageCommandExecution.Operation(a.Duration, a.Operation, a.Group))), a.Wait, c);
}

[RuntimeCommand("transition", Category = "Stage", Usage = "transition <kind> [appearance:<id>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageTransitionCommandArguments
{
    [CommandParameter("kind", 0, Primary = true, Required = true)] public StageTransitionKind Kind { get; set; }
    [CommandParameter("appearance", 1)] public string? Appearance { get; set; }
    [CommandParameter("duration", 2, DefaultValue = "0.5", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 3)] public bool Wait { get; set; }
    [CommandParameter("group", 4)] public string? Group { get; set; }
    [CommandParameter("operation", 5)] public string? Operation { get; set; }
}
public sealed class StageTransitionCommandHandler : IRuntimeCommandHandler<StageTransitionCommandArguments>
{
    public Task ExecuteAsync(StageTransitionCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IStageTransitionManager>(c).Run(new StageTransitionRequest(
                a.Kind, Colors.Black, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group), a.Appearance)), a.Wait, c);
}

[RuntimeCommand("curtain", Category = "Stage", Usage = "curtain <open|closed> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageCurtainCommandArguments
{
    [CommandParameter("state", 0, Primary = true, Required = true)] public StageCurtainPosition Position { get; set; }
    [CommandParameter("duration", 1, DefaultSource = CommandDefaultSource.AnimationDuration, Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class StageCurtainCommandHandler : IRuntimeCommandHandler<StageCurtainCommandArguments>
{
    public Task ExecuteAsync(StageCurtainCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IStageCurtainManager>(c).SetCurtain(
                Colors.Black,
                a.Position == StageCurtainPosition.Closed ? 1 : 0,
                StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
}

[RuntimeCommand("overlay", Category = "Stage", Usage = "overlay <opacity> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageOverlayCommandArguments
{
    [CommandParameter("opacity", 0, Primary = true, Required = true, Minimum = 0)] public double Opacity { get; set; }
    [CommandParameter("duration", 1, DefaultValue = "0.35", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class StageOverlayCommandHandler : IRuntimeCommandHandler<StageOverlayCommandArguments>
{
    public Task ExecuteAsync(StageOverlayCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IScreenEffectManager>(c).SetOverlay(
                Colors.Black, (float)a.Opacity, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
}

[RuntimeCommand("letterbox", Category = "Stage", Usage = "letterbox <ratio> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageLetterboxCommandArguments
{
    [CommandParameter("ratio", 0, Primary = true, Required = true, Minimum = 0)] public double Ratio { get; set; }
    [CommandParameter("duration", 1, DefaultValue = "0.35", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class StageLetterboxCommandHandler : IRuntimeCommandHandler<StageLetterboxCommandArguments>
{
    public Task ExecuteAsync(StageLetterboxCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IScreenEffectManager>(c).SetLetterbox((float)a.Ratio, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
}

[RuntimeCommand("flash", Category = "Stage", Usage = "flash [intensity:<0..1>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageFlashCommandArguments
{
    [CommandParameter("intensity", 0, DefaultValue = "1", Minimum = 0)] public double Intensity { get; set; }
    [CommandParameter("duration", 1, DefaultValue = "0.2", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class StageFlashCommandHandler : IRuntimeCommandHandler<StageFlashCommandArguments>
{
    public Task ExecuteAsync(StageFlashCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IScreenEffectManager>(c).Flash(
                Colors.White, (float)a.Intensity, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
}
