#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Configuration;
using Godot;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class StageCommandModule : IRuntimeCommandModule
{
    public void Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands
            .Register(new StageTransitionCommandHandler())
            .Register(new StageOverlayCommandHandler())
            .Register(new StageLetterboxCommandHandler());
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

    public static T Require<T>(RuntimeCommandContext context) where T : class =>
        context.Services?.Get<T>() ?? throw new RuntimeCommandException(
            "stage", $"Stage service '{typeof(T).Name}' is not available.");
}

[RuntimeCommand("transition", Category = "Stage", Usage = "transition <kind> [appearance:<id>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageTransitionCommandArguments
{
    [CommandParameter("kind", 0, Primary = true, Required = true)] public StageTransitionKind Kind { get; set; }
    [CommandParameter("appearance", 1)] public string? Appearance { get; set; }
    [CommandParameter("duration", 2, DefaultValue = RuntimeDefaults.TransitionDurationSecondsText, Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 3)] public bool Wait { get; set; }
    [CommandParameter("group", 4)] public string? Group { get; set; }
    [CommandParameter("operation", 5)] public string? Operation { get; set; }
}
public sealed class StageTransitionCommandHandler : IRuntimeCommandHandler<StageTransitionCommandArguments>
{
    public Task ExecuteAsync(StageTransitionCommandArguments arguments, RuntimeCommandContext context) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IStageTransitionManager>(context).Run(new StageTransitionRequest(
                arguments.Kind, Colors.Black, StageCommandExecution.Operation(arguments.Duration, arguments.Operation, arguments.Group), arguments.Appearance)), arguments.Wait, context);
}

[RuntimeCommand("overlay", Category = "Stage", Usage = "overlay <opacity> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageOverlayCommandArguments
{
    [CommandParameter("opacity", 0, Primary = true, Required = true, Minimum = 0)] public double Opacity { get; set; }
    [CommandParameter("duration", 1, DefaultValue = RuntimeDefaults.AnimationDurationSecondsText, Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class StageOverlayCommandHandler : IRuntimeCommandHandler<StageOverlayCommandArguments>
{
    public Task ExecuteAsync(StageOverlayCommandArguments arguments, RuntimeCommandContext context) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IScreenEffectManager>(context).SetOverlay(
                Colors.Black, (float)arguments.Opacity, StageCommandExecution.Operation(arguments.Duration, arguments.Operation, arguments.Group)), arguments.Wait, context);
}

[RuntimeCommand("letterbox", Category = "Stage", Usage = "letterbox <ratio> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageLetterboxCommandArguments
{
    [CommandParameter("ratio", 0, Primary = true, Required = true, Minimum = 0)] public double Ratio { get; set; }
    [CommandParameter("duration", 1, DefaultValue = RuntimeDefaults.AnimationDurationSecondsText, Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class StageLetterboxCommandHandler : IRuntimeCommandHandler<StageLetterboxCommandArguments>
{
    public Task ExecuteAsync(StageLetterboxCommandArguments arguments, RuntimeCommandContext context) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IScreenEffectManager>(context).SetLetterbox((float)arguments.Ratio, StageCommandExecution.Operation(arguments.Duration, arguments.Operation, arguments.Group)), arguments.Wait, context);
}
