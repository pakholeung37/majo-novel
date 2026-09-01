#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Configuration;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class StageCommandModule : IRuntimeCommandModule
{
    private readonly bool includeTransientCommands;

    public StageCommandModule(bool includeTransientCommands = true)
    {
        this.includeTransientCommands = includeTransientCommands;
    }

    public void Register(IRuntimeCommandRegistrar commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        commands
            .Register(new StageTransitionCommandHandler())
            .Register(new StageOverlayCommandHandler())
            .Register(new StageLetterboxCommandHandler());
        if (includeTransientCommands)
        {
            commands
                .Register(new StageCurtainCommandHandler())
                .Register(new StageFlashCommandHandler());
        }
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
    public Task ExecuteAsync(StageCurtainCommandArguments a, RuntimeCommandContext c)
    {
        var beats = c.Services?.Get<IDirectionBeatRuntime>();
        if (beats?.HasOpenBeat == true)
            return beats.SubmitAsync(new CurtainCue(
                a.Position == StageCurtainPosition.Open,
                DirectionColor.Black,
                a.Duration),
                a.Wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
                c.CancellationToken);
        return
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IStageCurtainManager>(c).SetCurtain(
                Colors.Black,
                a.Position == StageCurtainPosition.Closed ? 1 : 0,
                StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
    }
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
    public Task ExecuteAsync(StageOverlayCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IScreenEffectManager>(c).SetOverlay(
                Colors.Black, (float)a.Opacity, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
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
    public Task ExecuteAsync(StageLetterboxCommandArguments a, RuntimeCommandContext c) =>
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IScreenEffectManager>(c).SetLetterbox((float)a.Ratio, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
}

[RuntimeCommand("flash", Category = "Stage", Usage = "flash [intensity:<0..1>] [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class StageFlashCommandArguments
{
    [CommandParameter("intensity", 0, DefaultValue = "1", Minimum = 0)] public double Intensity { get; set; }
    [CommandParameter("duration", 1, DefaultValue = RuntimeDefaults.FlashDurationSecondsText, Minimum = 0)] public double Duration { get; set; }
    [CommandParameter("wait", 2)] public bool Wait { get; set; }
    [CommandParameter("group", 3)] public string? Group { get; set; }
    [CommandParameter("operation", 4)] public string? Operation { get; set; }
}
public sealed class StageFlashCommandHandler : IRuntimeCommandHandler<StageFlashCommandArguments>
{
    public Task ExecuteAsync(StageFlashCommandArguments a, RuntimeCommandContext c)
    {
        var beats = c.Services?.Get<IDirectionBeatRuntime>();
        if (beats?.HasOpenBeat == true)
            return beats.SubmitAsync(new FlashCue(
                DirectionColor.White,
                (float)a.Intensity,
                a.Duration),
                a.Wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
                c.CancellationToken);
        return
        StageCommandExecution.TrackAndWaitAsync(
            StageCommandExecution.Require<IScreenEffectManager>(c).Flash(
                Colors.White, (float)a.Intensity, StageCommandExecution.Operation(a.Duration, a.Operation, a.Group)), a.Wait, c);
    }
}
