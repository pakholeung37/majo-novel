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
        double duration, string? operation, string? group, bool skippable = true) =>
        new StageOperationSpec(duration, operation, group, skippable).Validate();

    public static async Task TrackAndWaitAsync(
        Majo.Runtime.Operations.IOperationHandle operation,
        bool wait,
        RuntimeCommandContext context)
    {
        context.Track(operation);
        if (wait) await operation.Completion.WaitAsync(context.CancellationToken);
    }

    public static T Require<T>(RuntimeCommandContext context) where T : class =>
        context.Services?.Get<T>() ?? throw new RuntimeCommandException(
            "stage", $"Stage service '{typeof(T).Name}' is not available.");
}

[RuntimeCommand("cameraMove", Category = "Stage", Usage = "cameraMove <x> <y> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraMoveCommandArguments
{
    [CommandParameter("x", 0, Primary = true, Required = true)] public double X { get; set; }
    [CommandParameter("y", 1, Required = true)] public double Y { get; set; }
    [CommandParameter("duration", 2, DefaultValue = "0.35", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 3)] public bool Wait { get; set; }
    [CommandParameter("group", 4)] public string? Group { get; set; }
    [CommandParameter("operation", 5)] public string? Operation { get; set; }
}

public sealed class CameraMoveCommandHandler : IRuntimeCommandHandler<CameraMoveCommandArguments>
{
    public Task ExecuteAsync(CameraMoveCommandArguments a, RuntimeCommandContext c) =>
        ExecuteAsync(a, c, StageCommandExecution.Require<ICameraManager>(c).MoveTo(
            new Vector2((float)a.X, (float)a.Y), StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)));

    private static Task ExecuteAsync(CameraMoveCommandArguments a, RuntimeCommandContext c, Majo.Runtime.Operations.IOperationHandle op) =>
        StageCommandExecution.TrackAndWaitAsync(op, a.Wait, c);
}

[RuntimeCommand("cameraZoom", Category = "Stage", Usage = "cameraZoom <zoom> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraZoomCommandArguments
{
    [CommandParameter("zoom", 0, Primary = true, Required = true, Minimum = 0.001)] public double Zoom { get; set; }
    [CommandParameter("duration", 1, DefaultValue = "0.35", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class CameraZoomCommandHandler : IRuntimeCommandHandler<CameraZoomCommandArguments>
{
    public Task ExecuteAsync(CameraZoomCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).ZoomTo((float)a.Zoom, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
}

[RuntimeCommand("cameraRotate", Category = "Stage", Usage = "cameraRotate <degrees> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraRotateCommandArguments
{
    [CommandParameter("degrees", 0, Primary = true, Required = true)] public double Degrees { get; set; }
    [CommandParameter("duration", 1, DefaultValue = "0.35", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class CameraRotateCommandHandler : IRuntimeCommandHandler<CameraRotateCommandArguments>
{
    public Task ExecuteAsync(CameraRotateCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).RotateTo((float)a.Degrees, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
}

[RuntimeCommand("cameraReset", Category = "Stage", Usage = "cameraReset [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraResetCommandArguments
{
    [CommandParameter("duration", 0, DefaultValue = "0.35", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 1)] public bool Wait { get; set; }
    [CommandParameter("group", 2)] public string? Group { get; set; }
    [CommandParameter("operation", 3)] public string? Operation { get; set; }
}
public sealed class CameraResetCommandHandler : IRuntimeCommandHandler<CameraResetCommandArguments>
{
    public Task ExecuteAsync(CameraResetCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).ResetCamera(StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
}

[RuntimeCommand("cameraShake", Category = "Stage", Usage = "cameraShake [strength:<number>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CameraShakeCommandArguments
{
    [CommandParameter("strength", 0, DefaultValue = "8", Minimum = 0)] public double Strength { get; set; }
    [CommandParameter("duration", 1, DefaultValue = "0.4", Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class CameraShakeCommandHandler : IRuntimeCommandHandler<CameraShakeCommandArguments>
{
    public Task ExecuteAsync(CameraShakeCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<ICameraManager>(c).Shake(new CameraShakeSpec(
                new Vector2((float)a.Strength, (float)a.Strength), 20,
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
