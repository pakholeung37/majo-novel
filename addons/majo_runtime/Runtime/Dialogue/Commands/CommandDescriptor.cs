#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CommandDescriptor
{
    private CommandDescriptor(
        IReadOnlyList<string> path,
        string? category,
        Type argumentsType,
        IReadOnlyList<CommandParameterDescriptor> parameters,
        IReadOnlyList<CommandParameterSetDescriptor> parameterSets,
        string usage)
    {
        Path = path;
        Category = category;
        ArgumentsType = argumentsType;
        Parameters = parameters;
        ParameterSets = parameterSets;
        Usage = usage;
    }

    public string Name => string.Join(' ', Path);

    public IReadOnlyList<string> Path { get; }

    public string RootName => Path[0];

    public string? Category { get; }

    public Type ArgumentsType { get; }

    public IReadOnlyList<CommandParameterDescriptor> Parameters { get; }

    public IReadOnlyList<CommandParameterSetDescriptor> ParameterSets { get; }

    public string Usage { get; }

    internal static CommandDescriptor Create<TArguments>()
        where TArguments : class, new() => Create(typeof(TArguments));

    private static CommandDescriptor Create(Type argumentsType)
    {
        var command = argumentsType.GetCustomAttribute<RuntimeCommandAttribute>() ??
            throw new InvalidOperationException(
                $"Command arguments '{argumentsType.FullName}' must declare " +
                $"{nameof(RuntimeCommandAttribute)}.");
        var publicProperties = argumentsType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var unannotated = publicProperties.FirstOrDefault(property =>
            property.SetMethod?.IsPublic == true &&
            property.GetCustomAttribute<CommandParameterAttribute>() is null);
        if (unannotated is not null)
        {
            throw new InvalidOperationException(
                $"Writable command argument property '{argumentsType.Name}." +
                $"{unannotated.Name}' must declare {nameof(CommandParameterAttribute)}.");
        }

        var parameters = publicProperties
            .Select(property => (
                Property: property,
                Attribute: property.GetCustomAttribute<CommandParameterAttribute>()))
            .Where(item => item.Attribute is not null)
            .Select(item => new CommandParameterDescriptor(
                item.Attribute!.Name,
                item.Attribute.Order,
                item.Attribute.Primary,
                item.Attribute.Required,
                item.Attribute.ValueHint,
                item.Attribute.DefaultSource,
                item.Attribute.DefaultValue,
                item.Attribute.Minimum,
                item.Property))
            .OrderBy(parameter => parameter.Order)
            .ToArray();
        ValidateParameters(command.Name, argumentsType, parameters);

        var parameterNames = new HashSet<string>(
            parameters.Select(parameter => parameter.Name),
            StringComparer.OrdinalIgnoreCase);
        var parameterSets = argumentsType
            .GetCustomAttributes<CommandParameterSetAttribute>()
            .Select(attribute => CreateSet(command.Name, attribute, parameterNames))
            .ToArray();
        var usage = string.IsNullOrWhiteSpace(command.Usage)
            ? BuildUsage(command.Name, parameters)
            : command.Usage;
        return new CommandDescriptor(
            command.Path,
            command.Category,
            argumentsType,
            Array.AsReadOnly(parameters),
            Array.AsReadOnly(parameterSets),
            usage!);
    }

    private static void ValidateParameters(
        string commandName,
        Type argumentsType,
        IReadOnlyList<CommandParameterDescriptor> parameters)
    {
        var duplicate = parameters
            .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Command '{commandName}' declares parameter '{duplicate.Key}' more than once.");
        }

        if (parameters.Count(parameter => parameter.Primary) > 1)
        {
            throw new InvalidOperationException(
                $"Command '{commandName}' declares more than one primary parameter.");
        }

        var duplicateOrder = parameters
            .GroupBy(parameter => parameter.Order)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateOrder is not null)
        {
            throw new InvalidOperationException(
                $"Command '{commandName}' declares parameter order " +
                $"'{duplicateOrder.Key}' more than once.");
        }

        foreach (var parameter in parameters)
        {
            if (parameter.Property.SetMethod is null || !parameter.Property.SetMethod.IsPublic)
            {
                throw new InvalidOperationException(
                    $"Command parameter '{argumentsType.Name}.{parameter.Property.Name}' " +
                    "must have a public setter.");
            }

            if (parameter.Primary && !parameter.Required)
            {
                throw new InvalidOperationException(
                    $"Primary parameter '{parameter.Name}' on command '{commandName}' " +
                    "must be required.");
            }

            if (parameter.DefaultSource != CommandDefaultSource.None &&
                parameter.DefaultValue is not null)
            {
                throw new InvalidOperationException(
                    $"Parameter '{parameter.Name}' on command '{commandName}' " +
                    "cannot declare both a default source and a constant default.");
            }

            if (parameter.Required &&
                (parameter.DefaultSource != CommandDefaultSource.None ||
                 parameter.DefaultValue is not null))
            {
                throw new InvalidOperationException(
                    $"Required parameter '{parameter.Name}' on command '{commandName}' " +
                    "cannot declare a default.");
            }
        }
    }

    private static CommandParameterSetDescriptor CreateSet(
        string commandName,
        CommandParameterSetAttribute attribute,
        HashSet<string> parameterNames)
    {
        var names = attribute.Parameters
            .Select(name =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                if (!parameterNames.Contains(name))
                {
                    throw new InvalidOperationException(
                        $"Command '{commandName}' parameter set references unknown " +
                        $"parameter '{name}'.");
                }

                return name;
            })
            .ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
        {
            throw new InvalidOperationException(
                $"Command '{commandName}' parameter set contains duplicate names.");
        }

        return new CommandParameterSetDescriptor(attribute.Rule, Array.AsReadOnly(names));
    }

    private static string BuildUsage(
        string commandName,
        IReadOnlyList<CommandParameterDescriptor> parameters)
    {
        var parts = new List<string> { commandName };
        foreach (var parameter in parameters)
        {
            var text = parameter.Primary
                ? $"<{parameter.Name}>"
                : $"{parameter.Name}:<{parameter.ValueHint}>";
            parts.Add(parameter.Required ? text : $"[{text}]");
        }

        return string.Join(' ', parts);
    }
}

public sealed class CommandParameterDescriptor
{
    internal CommandParameterDescriptor(
        string name,
        int order,
        bool primary,
        bool required,
        string valueHint,
        CommandDefaultSource defaultSource,
        string? defaultValue,
        double minimum,
        PropertyInfo property)
    {
        Name = name;
        Order = order;
        Primary = primary;
        Required = required;
        ValueHint = valueHint;
        DefaultSource = defaultSource;
        DefaultValue = defaultValue;
        Minimum = minimum;
        Property = property;
    }

    public string Name { get; }

    public int Order { get; }

    public bool Primary { get; }

    public bool Required { get; }

    public string ValueHint { get; }

    public CommandDefaultSource DefaultSource { get; }

    public string? DefaultValue { get; }

    public double Minimum { get; }

    public Type ValueType => Property.PropertyType;

    internal PropertyInfo Property { get; }
}

public sealed record CommandParameterSetDescriptor(
    CommandParameterSetRule Rule,
    IReadOnlyList<string> Parameters);
