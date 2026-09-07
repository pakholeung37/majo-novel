#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

[Command("beat begin", Category = "Direction")]
public sealed partial class BeatBeginCommand : RuntimeCommand<BeatBeginCommand.Arguments>
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
        [Option] CommandValue<string> Id,
        [Option(Minimum = 0, ValueHint = "seconds")] CommandValue<double> Duration,
        [Option(ValueHint = "curve")] CommandValue<EasingId> Ease);
}

[Command("beat end", Category = "Direction")]
public sealed partial class BeatEndCommand : RuntimeCommand<BeatEndCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;

    public BeatEndCommand(IDirectionBeatRuntime beats)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var wait = arguments.Wait.Wait.GetValueOrDefault(true);
        await beats.EndAsync(
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments([Group(Configuration = typeof(BlockingWait))] WaitArguments Wait);
}

[Command("beat join", Category = "Direction")]
public sealed partial class BeatJoinCommand : RuntimeCommand<BeatJoinCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;

    public BeatJoinCommand(IDirectionBeatRuntime beats)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        await beats.JoinAsync(arguments.Id, context.CancellationToken);
    }

    public sealed record Arguments([Primary] string Id);
}

[Command("beat finish", Category = "Direction")]
public sealed partial class BeatFinishCommand : RuntimeCommand<BeatFinishCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;

    public BeatFinishCommand(IDirectionBeatRuntime beats)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        await beats.FinishAsync(arguments.Id, context.CancellationToken);
    }

    public sealed record Arguments([Primary] string Id);
}

[Command("beat abort", Category = "Direction")]
public sealed partial class BeatAbortCommand : RuntimeCommand<BeatAbortCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;

    public BeatAbortCommand(IDirectionBeatRuntime beats)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        beats.AbortOpen();
        return ValueTask.CompletedTask;
    }

    public sealed record Arguments;
}
