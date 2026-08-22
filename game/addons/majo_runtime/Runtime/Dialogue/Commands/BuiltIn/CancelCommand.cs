#nullable enable

using System.Threading.Tasks;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

[RuntimeCommand(
    "cancel",
    Category = "Playback",
    Usage = "cancel <group:<id>|operation:<id>>")]
[CommandParameterSet(CommandParameterSetRule.ExactlyOne, "group", "operation")]
public sealed class CancelCommandArguments
{
    [CommandParameter("group", 0, ValueHint = "id")]
    public string? Group { get; set; }

    [CommandParameter("operation", 1, ValueHint = "id")]
    public string? Operation { get; set; }
}

public sealed class CancelCommandHandler : IRuntimeCommandHandler<CancelCommandArguments>
{
    public Task ExecuteAsync(
        CancelCommandArguments arguments,
        RuntimeCommandContext context)
    {
        var operations = context.Require(context.Operations, "OperationScheduler");
        var target = arguments.Group is not null
            ? OperationTarget.ForGroup(arguments.Group)
            : OperationTarget.ForId(arguments.Operation!);
        return operations.CancelAsync(target, context.CancellationToken);
    }
}
