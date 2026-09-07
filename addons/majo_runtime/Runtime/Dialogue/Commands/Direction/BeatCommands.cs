#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class BeatBeginCommand : RuntimeCommand<BeatBeginCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private long anonymousBeatSequence;

    public BeatBeginCommand(
        IDirectionBeatRuntime beats,
        IDialogueNodeAccessor nodeAccessor)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "beat begin",
                category: "Direction",
                usage: "beat begin [id:<id>] [duration:<seconds>] [ease:<curve>]")
            .Optional<string>("id")
            .Optional<double>("duration", minimum: 0)
            .Optional<EasingId>("ease")
            .Build(static values => new Arguments(
                values.Optional<string>("id"),
                values.Optional<double>("duration"),
                values.Optional<EasingId>("ease")));

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.CurrentNode;
        if (string.IsNullOrWhiteSpace(node))
        {
            throw new DirectionBeatException("Cannot begin a Beat outside a Yarn Node.");
        }

        var beatId = arguments.Id.WasProvided && !string.IsNullOrWhiteSpace(arguments.Id.Value)
            ? arguments.Id.Value
            : $"{node.Trim()}#anonymous-{Interlocked.Increment(ref anonymousBeatSequence)}";

        double? duration = arguments.Duration.WasProvided ? arguments.Duration.Value : null;
        EasingId? ease = (arguments.Ease.WasProvided && !arguments.Ease.Value.IsEmpty) ? arguments.Ease.Value : (EasingId?)null;

        beats.Begin(
            beatId,
            node,
            new DirectionBeatDefaults(duration, ease));

        return ValueTask.CompletedTask;
    }

    public sealed record Arguments(
        CommandValue<string> Id,
        CommandValue<double> Duration,
        CommandValue<EasingId> Ease);
}

public sealed class BeatEndCommand : RuntimeCommand<BeatEndCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;

    public BeatEndCommand(IDirectionBeatRuntime beats)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("beat end", category: "Direction", usage: "beat end [wait:<bool>]")
            .Use("wait", CommandParameterGroups.Wait(contractDefault: true, order: 0))
            .Build(static values => new Arguments(values.Group<WaitArguments>("wait")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var wait = arguments.Wait.Wait.GetValueOrDefault(true);
        await beats.EndAsync(
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(WaitArguments Wait);
}

public sealed class BeatJoinCommand : RuntimeCommand<BeatJoinCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;

    public BeatJoinCommand(IDirectionBeatRuntime beats)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("beat join", category: "Direction", usage: "beat join <id>")
            .Primary<string>("id")
            .Build(static values => new Arguments(values.Required<string>("id")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        await beats.JoinAsync(arguments.Id, context.CancellationToken);
    }

    public sealed record Arguments(string Id);
}

public sealed class BeatFinishCommand : RuntimeCommand<BeatFinishCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;

    public BeatFinishCommand(IDirectionBeatRuntime beats)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("beat finish", category: "Direction", usage: "beat finish <id>")
            .Primary<string>("id")
            .Build(static values => new Arguments(values.Required<string>("id")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        await beats.FinishAsync(arguments.Id, context.CancellationToken);
    }

    public sealed record Arguments(string Id);
}

public sealed class BeatAbortCommand : RuntimeCommand<BeatAbortCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;

    public BeatAbortCommand(IDirectionBeatRuntime beats)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("beat abort", category: "Direction", usage: "beat abort")
            .Build(static _ => new Arguments());

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        beats.AbortOpen();
        return ValueTask.CompletedTask;
    }

    public sealed record Arguments;
}
