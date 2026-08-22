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
        "back <actor> appearance:<id> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]";
    public const string CharacterUsage =
        "char <actor> appearance:<id> position:<left|center|right> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]";
    public const string HideUsage =
        "hide <actor> [duration:<seconds>] [wait:<bool>] [group:<id>] [operation:<id>]";
    public const string AwaitUsage =
        "await [group:<id>|operation:<id>] [complete:<bool>]";
    public const string EndAwaitUsage = "endAwait";
    public const string CancelUsage = "cancel <group:<id>|operation:<id>>";

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
        var parsed = NamedCommandArguments.Parse(
            "back",
            arguments,
            BackUsage,
            primaryCount: 1,
            "appearance",
            "duration",
            "wait",
            "group",
            "operation");
        return new BackCommand(
            parsed.Primary(0, "actor"),
            parsed.Required("appearance"),
            Duration("back", parsed, BackUsage),
            Boolean("back", parsed, "wait", false, BackUsage),
            parsed.OptionalIdentifier("group"),
            parsed.OptionalIdentifier("operation"));
    }

    public CharacterCommand ParseCharacter(IReadOnlyList<string> arguments)
    {
        var parsed = NamedCommandArguments.Parse(
            "char",
            arguments,
            CharacterUsage,
            primaryCount: 1,
            "appearance",
            "position",
            "duration",
            "wait",
            "group",
            "operation");
        return new CharacterCommand(
            parsed.Primary(0, "actor"),
            parsed.Required("appearance"),
            Position(parsed.Required("position")),
            Duration("char", parsed, CharacterUsage),
            Boolean("char", parsed, "wait", false, CharacterUsage),
            parsed.OptionalIdentifier("group"),
            parsed.OptionalIdentifier("operation"));
    }

    public HideCommand ParseHide(IReadOnlyList<string> arguments)
    {
        var parsed = NamedCommandArguments.Parse(
            "hide",
            arguments,
            HideUsage,
            primaryCount: 1,
            "duration",
            "wait",
            "group",
            "operation");
        return new HideCommand(
            parsed.Primary(0, "actor"),
            Duration("hide", parsed, HideUsage),
            Boolean("hide", parsed, "wait", false, HideUsage),
            parsed.OptionalIdentifier("group"),
            parsed.OptionalIdentifier("operation"));
    }

    public AwaitDirective ParseAwait(IReadOnlyList<string> arguments)
    {
        var parsed = NamedCommandArguments.Parse(
            "await",
            arguments,
            AwaitUsage,
            primaryCount: 0,
            "group",
            "operation",
            "complete");
        var complete = Boolean("await", parsed, "complete", false, AwaitUsage);
        var target = Target("await", parsed, required: false, AwaitUsage);
        return target is null
            ? new BeginAwaitDirective(complete)
            : new TargetAwaitDirective(new AwaitCommand(target.Value, complete));
    }

    public void ParseEndAwait(IReadOnlyList<string> arguments) =>
        _ = NamedCommandArguments.Parse(
            "endAwait",
            arguments,
            EndAwaitUsage,
            primaryCount: 0);

    public CancelCommand ParseCancel(IReadOnlyList<string> arguments)
    {
        var parsed = NamedCommandArguments.Parse(
            "cancel",
            arguments,
            CancelUsage,
            primaryCount: 0,
            "group",
            "operation");
        return new CancelCommand(
            Target("cancel", parsed, required: true, CancelUsage)!.Value);
    }

    private double Duration(
        string command,
        NamedCommandArguments arguments,
        string usage)
    {
        var value = arguments.Optional("duration");
        if (value is null)
        {
            return defaultDurationSeconds;
        }

        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration) ||
            !double.IsFinite(duration) ||
            duration < 0)
        {
            throw Invalid(
                command,
                $"duration '{value}' must be a finite, non-negative number",
                usage);
        }

        return duration;
    }

    private static bool Boolean(
        string command,
        NamedCommandArguments arguments,
        string parameter,
        bool defaultValue,
        string usage)
    {
        var text = arguments.Optional(parameter);
        if (text is null)
        {
            return defaultValue;
        }

        if (!bool.TryParse(text, out var value))
        {
            throw Invalid(
                command,
                $"{parameter} '{text}' must be true or false",
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

    private static OperationTarget? Target(
        string command,
        NamedCommandArguments arguments,
        bool required,
        string usage)
    {
        var group = arguments.OptionalIdentifier("group");
        var operation = arguments.OptionalIdentifier("operation");
        if (group is not null && operation is not null)
        {
            throw Invalid(
                command,
                "group and operation are mutually exclusive",
                usage);
        }

        if (group is not null)
        {
            return OperationTarget.ForGroup(group);
        }

        if (operation is not null)
        {
            return OperationTarget.ForId(operation);
        }

        if (required)
        {
            throw Invalid(
                command,
                "either group or operation is required",
                usage);
        }

        return null;
    }

    internal static RuntimeCommandException Invalid(
        string command,
        string message,
        string usage) =>
        new(command, $"{message}. Usage: {usage}.");
}
