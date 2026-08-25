#nullable enable

using System;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

[RuntimeCommand(
    "hide",
    Category = "Actor",
    Usage = "hide <actor> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class HideCommandArguments
{
    [CommandParameter("actor", 0, Primary = true, Required = true)]
    public string Actor { get; set; } = string.Empty;

    [CommandParameter(
        "duration",
        1,
        ValueHint = "seconds",
        DefaultSource = CommandDefaultSource.AnimationDuration,
        Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 2, ValueHint = "bool")]
    public bool Wait { get; set; }

    [CommandParameter("group", 3, ValueHint = "id")]
    public string? Group { get; set; }

    [CommandParameter("operation", 4, ValueHint = "id")]
    public string? Operation { get; set; }
}

public sealed class HideCommandHandler : IRuntimeCommandHandler<HideCommandArguments>
{
    private readonly IActorCommandService commands;

    public HideCommandHandler(IActorCommandService commands)
    {
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public Task ExecuteAsync(
        HideCommandArguments arguments,
        RuntimeCommandContext context) => commands.HideAsync(
            new HideCommand(
                arguments.Actor,
                arguments.Duration,
                arguments.Wait,
                arguments.Group,
                arguments.Operation),
            context);
}
