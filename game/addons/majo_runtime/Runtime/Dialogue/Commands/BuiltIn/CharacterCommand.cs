#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Actors;

namespace Majo.Runtime.Dialogue.Commands;

[RuntimeCommand(
    "char",
    Category = "Actor",
    Usage = "char <actor> appearance:<id> position:<left|center|right> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]")]
public sealed class CharacterCommandArguments
{
    [CommandParameter("actor", 0, Primary = true, Required = true)]
    public string Actor { get; set; } = string.Empty;

    [CommandParameter("appearance", 1, Required = true, ValueHint = "id")]
    public string Appearance { get; set; } = string.Empty;

    [CommandParameter("position", 2, Required = true, ValueHint = "left|center|right")]
    public CharacterPositionPreset Position { get; set; }

    [CommandParameter(
        "duration",
        3,
        ValueHint = "seconds",
        DefaultSource = CommandDefaultSource.AnimationDuration,
        Minimum = 0)]
    public double Duration { get; set; }

    [CommandParameter("wait", 4, ValueHint = "bool")]
    public bool Wait { get; set; }

    [CommandParameter("group", 5, ValueHint = "id")]
    public string? Group { get; set; }

    [CommandParameter("operation", 6, ValueHint = "id")]
    public string? Operation { get; set; }
}

public sealed class CharacterCommandHandler
    : IRuntimeCommandHandler<CharacterCommandArguments>
{
    private readonly IActorCommandService commands;

    public CharacterCommandHandler(IActorCommandService commands)
    {
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public Task ExecuteAsync(
        CharacterCommandArguments arguments,
        RuntimeCommandContext context) => commands.CharacterAsync(
            new CharacterCommand(
                arguments.Actor,
                arguments.Appearance,
                arguments.Position,
                arguments.Duration,
                arguments.Wait,
                arguments.Group,
                arguments.Operation),
            context);
}
