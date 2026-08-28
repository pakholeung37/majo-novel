#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Dialogue.Commands;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RuntimeCommandAttribute : Attribute
{
    public RuntimeCommandAttribute(params string[] path)
    {
        if (path is null || path.Length == 0)
        {
            throw new ArgumentException(
                "Command paths require at least one segment.",
                nameof(path));
        }

        for (var index = 0; index < path.Length; index++)
        {
            var segment = path[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);
            if (segment.Any(character => char.IsWhiteSpace(character) || character == ':'))
            {
                throw new ArgumentException(
                    "Command path segments cannot contain whitespace or colons.",
                    nameof(path));
            }
        }

        Path = Array.AsReadOnly(path.ToArray());
    }

    public IReadOnlyList<string> Path { get; }

    public string Name => string.Join(' ', Path);

    public string? Category { get; init; }

    public string? Usage { get; init; }
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class CommandParameterAttribute : Attribute
{
    public CommandParameterAttribute(string name, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(character => char.IsWhiteSpace(character) || character == ':'))
        {
            throw new ArgumentException(
                "Command parameter names cannot contain whitespace or colons.",
                nameof(name));
        }

        Name = name;
        Order = order;
    }

    public string Name { get; }

    public int Order { get; }

    public bool Primary { get; init; }

    public bool Required { get; init; }

    public string ValueHint { get; init; } = "value";

    public CommandDefaultSource DefaultSource { get; init; }

    public string? DefaultValue { get; init; }

    public double Minimum { get; init; } = double.NaN;
}

public enum CommandDefaultSource
{
    None,
    AnimationDuration,
    CharacterAnimationDuration,
    AudioFadeDuration,
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CommandParameterSetAttribute : Attribute
{
    public CommandParameterSetAttribute(
        CommandParameterSetRule rule,
        params string[] parameters)
    {
        if (parameters is null || parameters.Length < 2)
        {
            throw new ArgumentException(
                "A command parameter set requires at least two parameters.",
                nameof(parameters));
        }

        Rule = rule;
        Parameters = parameters;
    }

    public CommandParameterSetRule Rule { get; }

    public IReadOnlyList<string> Parameters { get; }
}

public enum CommandParameterSetRule
{
    AtMostOne,
    ExactlyOne,
    AtLeastOne,
}
