#nullable enable

using System.Threading.Tasks;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

[RuntimeCommand(
    "await",
    Category = "Playback",
    Usage = "await [group:<id>|operation:<id>] [complete:<bool>]")]
[CommandParameterSet(CommandParameterSetRule.AtMostOne, "group", "operation")]
public sealed class AwaitCommandArguments
{
    [CommandParameter("group", 0, ValueHint = "id")]
    public string? Group { get; set; }

    [CommandParameter("operation", 1, ValueHint = "id")]
    public string? Operation { get; set; }

    [CommandParameter("complete", 2, ValueHint = "bool")]
    public bool Complete { get; set; }
}

public sealed class AwaitCommandHandler : IRuntimeCommandHandler<AwaitCommandArguments>
{
    public Task ExecuteAsync(
        AwaitCommandArguments arguments,
        RuntimeCommandContext context)
    {
        var target = CreateTarget(arguments);
        if (target is null)
        {
            context.BeginAwait(arguments.Complete);
            return Task.CompletedTask;
        }

        var operations = context.Require(context.Operations, "OperationScheduler");
        return arguments.Complete
            ? operations.CompleteAsync(target.Value, context.CancellationToken)
            : operations.WaitAsync(target.Value, context.CancellationToken);
    }

    private static OperationTarget? CreateTarget(AwaitCommandArguments arguments)
    {
        if (arguments.Group is not null)
        {
            return OperationTarget.ForGroup(arguments.Group);
        }

        return arguments.Operation is not null
            ? OperationTarget.ForId(arguments.Operation)
            : null;
    }
}
