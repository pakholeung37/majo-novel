#nullable enable

using System;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

[RuntimeCommand(
    "back",
    Category = "Actor",
    Usage = "back <actor> appearance:<id> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class BackCommandArguments
{
    [CommandParameter("actor", 0, Primary = true, Required = true)]
    public string Actor { get; set; } = string.Empty;

    [CommandParameter("appearance", 1, Required = true, ValueHint = "id")]
    public string Appearance { get; set; } = string.Empty;

    [CommandParameter(
        "duration",
        2,
        ValueHint = "seconds",
        DefaultSource = CommandDefaultSource.AnimationDuration,
        Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 3, ValueHint = "bool")]
    public bool Wait { get; set; }

    [CommandParameter("group", 4, ValueHint = "id")]
    public string? Group { get; set; }

    [CommandParameter("operation", 5, ValueHint = "id")]
    public string? Operation { get; set; }
}

public sealed class BackCommandHandler : IRuntimeCommandHandler<BackCommandArguments>
{
    private readonly IActorCommandService commands;

    public BackCommandHandler(IActorCommandService commands)
    {
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public Task ExecuteAsync(
        BackCommandArguments arguments,
        RuntimeCommandContext context) => commands.BackAsync(
            new BackCommand(
                arguments.Actor,
                arguments.Appearance,
                arguments.Duration,
                arguments.Wait,
                arguments.Group,
                arguments.Operation),
            context);
}
