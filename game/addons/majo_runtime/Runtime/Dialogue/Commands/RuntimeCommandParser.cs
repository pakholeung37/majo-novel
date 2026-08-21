#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Majo.Runtime.Actors;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RuntimeCommandParser
{
    public const string BackUsage =
        "back <actor> <appearance> [duration] [wait] [group|-] [operation|-]";
    public const string CharacterUsage =
        "char <actor> <appearance> <left|center|right> [duration] [wait] [group|-] [operation|-]";
    public const string HideUsage =
        "hide <actor> [duration] [wait] [group|-] [operation|-]";
    public const string AwaitUsage =
        "await <target> [complete] [group|id]";
    public const string CancelUsage =
        "cancel <target> [group|id]";

    private readonly double defaultDurationSeconds;

    public RuntimeCommandParser(double defaultDurationSeconds)
    {
        if (!double.IsFinite(defaultDurationSeconds) || defaultDurationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultDurationSeconds),
                "Default command duration must be finite and non-negative.");
        }

        this.defaultDurationSeconds = defaultDurationSeconds;
    }

    public BackCommand ParseBack(IReadOnlyList<string> arguments)
    {
        RequireCount("back", arguments, 2, 6, BackUsage);
        return new BackCommand(
            Required("back", arguments[0], "actor", BackUsage),
            Required("back", arguments[1], "appearance", BackUsage),
            Duration("back", arguments, 2, BackUsage),
            Boolean("back", arguments, 3, false, "wait", BackUsage),
            OptionalIdentifier(arguments, 4),
            OptionalIdentifier(arguments, 5));
    }

    public CharacterCommand ParseCharacter(IReadOnlyList<string> arguments)
    {
        RequireCount("char", arguments, 3, 7, CharacterUsage);
        return new CharacterCommand(
            Required("char", arguments[0], "actor", CharacterUsage),
            Required("char", arguments[1], "appearance", CharacterUsage),
            Position(arguments[2]),
            Duration("char", arguments, 3, CharacterUsage),
            Boolean("char", arguments, 4, false, "wait", CharacterUsage),
            OptionalIdentifier(arguments, 5),
            OptionalIdentifier(arguments, 6));
    }

    public HideCommand ParseHide(IReadOnlyList<string> arguments)
    {
        RequireCount("hide", arguments, 1, 5, HideUsage);
        return new HideCommand(
            Required("hide", arguments[0], "actor", HideUsage),
            Duration("hide", arguments, 1, HideUsage),
            Boolean("hide", arguments, 2, false, "wait", HideUsage),
            OptionalIdentifier(arguments, 3),
            OptionalIdentifier(arguments, 4));
    }

    public AwaitCommand ParseAwait(IReadOnlyList<string> arguments)
    {
        RequireCount("await", arguments, 1, 3, AwaitUsage);
        return new AwaitCommand(
            Target("await", arguments, 0, 2, AwaitUsage),
            Boolean("await", arguments, 1, false, "complete", AwaitUsage));
    }

    public CancelCommand ParseCancel(IReadOnlyList<string> arguments)
    {
        RequireCount("cancel", arguments, 1, 2, CancelUsage);
        return new CancelCommand(Target("cancel", arguments, 0, 1, CancelUsage));
    }

    private double Duration(
        string command,
        IReadOnlyList<string> arguments,
        int index,
        string usage)
    {
        if (arguments.Count <= index)
        {
            return defaultDurationSeconds;
        }

        if (!double.TryParse(
                arguments[index],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration) ||
            !double.IsFinite(duration) ||
            duration < 0)
        {
            throw Invalid(
                command,
                $"duration '{arguments[index]}' must be a finite, non-negative number",
                usage);
        }

        return duration;
    }

    private static bool Boolean(
        string command,
        IReadOnlyList<string> arguments,
        int index,
        bool defaultValue,
        string parameter,
        string usage)
    {
        if (arguments.Count <= index)
        {
            return defaultValue;
        }

        if (!bool.TryParse(arguments[index], out var value))
        {
            throw Invalid(
                command,
                $"{parameter} '{arguments[index]}' must be true or false",
                usage);
        }

        return value;
    }

    private static CharacterPositionPreset Position(string value)
    {
        if (value.Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            return CharacterPositionPreset.Left;
        }

        if (value.Equals("center", StringComparison.OrdinalIgnoreCase))
        {
            return CharacterPositionPreset.Center;
        }

        if (value.Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            return CharacterPositionPreset.Right;
        }

        throw Invalid(
            "char",
            $"position '{value}' must be left, center, or right",
            CharacterUsage);
    }

    private static OperationTarget Target(
        string command,
        IReadOnlyList<string> arguments,
        int targetIndex,
        int kindIndex,
        string usage)
    {
        var name = Required(command, arguments[targetIndex], "target", usage);
        var kind = arguments.Count > kindIndex ? arguments[kindIndex] : "group";
        if (kind.Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            return OperationTarget.ForGroup(name);
        }

        if (kind.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            return OperationTarget.ForId(name);
        }

        throw Invalid(
            command,
            $"target kind '{kind}' must be group or id",
            usage);
    }

    private static string Required(
        string command,
        string value,
        string parameter,
        string usage)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            throw Invalid(command, $"{parameter} is required", usage);
        }

        return value;
    }

    private static string? OptionalIdentifier(IReadOnlyList<string> arguments, int index)
    {
        if (arguments.Count <= index ||
            string.IsNullOrWhiteSpace(arguments[index]) ||
            arguments[index] == "-")
        {
            return null;
        }

        return arguments[index];
    }

    private static void RequireCount(
        string command,
        IReadOnlyCollection<string> arguments,
        int minimum,
        int maximum,
        string usage)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count < minimum || arguments.Count > maximum)
        {
            throw Invalid(
                command,
                $"expected {minimum} to {maximum} arguments, received {arguments.Count}",
                usage);
        }
    }

    private static RuntimeCommandException Invalid(
        string command,
        string message,
        string usage) =>
        new(command, $"{message}. Usage: {usage}.");
}
