#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Dialogue.Commands;

internal sealed class NamedCommandArguments
{
    private readonly string command;
    private readonly string usage;
    private readonly string[] primary;
    private readonly IReadOnlyDictionary<string, string> named;

    private NamedCommandArguments(
        string command,
        string usage,
        string[] primary,
        IReadOnlyDictionary<string, string> named)
    {
        this.command = command;
        this.usage = usage;
        this.primary = primary;
        this.named = named;
    }

    public static NamedCommandArguments Parse(
        string command,
        IReadOnlyList<string> arguments,
        string usage,
        int primaryCount,
        params string[] supportedNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(usage);
        ArgumentOutOfRangeException.ThrowIfNegative(primaryCount);

        var supported = new HashSet<string>(supportedNames, StringComparer.OrdinalIgnoreCase);
        var primary = new List<string>(primaryCount);
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf(':');
            if (separator < 0)
            {
                if (primary.Count >= primaryCount)
                {
                    throw RuntimeCommandParser.Invalid(
                        command,
                        $"unexpected positional argument '{argument}'",
                        usage);
                }

                primary.Add(RequiredValue(command, argument, "primary", usage));
                continue;
            }

            var name = argument[..separator];
            var value = argument[(separator + 1)..];
            if (string.IsNullOrWhiteSpace(name) || !supported.Contains(name))
            {
                throw RuntimeCommandParser.Invalid(
                    command,
                    $"unknown parameter '{name}'",
                    usage);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw RuntimeCommandParser.Invalid(
                    command,
                    $"parameter '{name}' requires a value",
                    usage);
            }

            if (!named.TryAdd(name, value))
            {
                throw RuntimeCommandParser.Invalid(
                    command,
                    $"parameter '{name}' is duplicated",
                    usage);
            }
        }

        if (primary.Count != primaryCount)
        {
            throw RuntimeCommandParser.Invalid(
                command,
                $"expected {primaryCount} positional argument(s), received {primary.Count}",
                usage);
        }

        return new NamedCommandArguments(command, usage, primary.ToArray(), named);
    }

    public string Primary(int index, string parameter) =>
        RequiredValue(command, primary[index], parameter, usage);

    public string Required(string parameter) => named.TryGetValue(parameter, out var value)
        ? RequiredValue(command, value, parameter, usage)
        : throw RuntimeCommandParser.Invalid(
            command,
            $"parameter '{parameter}' is required",
            usage);

    public string? Optional(string parameter) =>
        named.TryGetValue(parameter, out var value) ? value : null;

    public string? OptionalIdentifier(string parameter)
    {
        var value = Optional(parameter);
        if (value is null)
        {
            return null;
        }

        return RequiredValue(command, value, parameter, usage);
    }

    private static string RequiredValue(
        string command,
        string value,
        string parameter,
        string usage)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            throw RuntimeCommandParser.Invalid(
                command,
                $"{parameter} is required",
                usage);
        }

        return value;
    }
}
