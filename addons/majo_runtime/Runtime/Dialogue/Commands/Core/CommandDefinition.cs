#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Majo.Runtime.Dialogue.Commands;

public interface ICommandDefinition
{
    string Name { get; }

    IReadOnlyList<string> Path { get; }

    string RootName { get; }

    string? Category { get; }

    string Usage { get; }

    IReadOnlyList<CommandParameterDeclaration> Parameters { get; }

    IReadOnlyList<CommandGroupDeclaration> Groups { get; }

    IReadOnlyList<CommandParameterSetSpec> ParameterSets { get; }

    IReadOnlyList<CommandParameterDeclaration> FlattenedParameters { get; }
}

public sealed class CommandDefinition<TArguments> : ICommandDefinition
    where TArguments : class
{
    private readonly Func<BindingResolutionContext, Func<BindingSlotReader, TArguments>> binderFactory;
    private readonly IReadOnlyList<ICommandArgumentValidator<TArguments>> validators;

    public CommandDefinition(
        string name,
        IReadOnlyList<string> path,
        string? category,
        string usage,
        IReadOnlyList<CommandParameterDeclaration> parameters,
        IReadOnlyList<CommandGroupDeclaration> groups,
        IReadOnlyList<CommandParameterSetSpec> parameterSets,
        IReadOnlyList<CommandParameterDeclaration> flattenedParameters,
        Func<BindingResolutionContext, Func<BindingSlotReader, TArguments>> binderFactory,
        IReadOnlyList<ICommandArgumentValidator<TArguments>>? validators = null)
    {
        Name = name;
        Path = Array.AsReadOnly(path.ToArray());
        Category = category;
        Usage = usage;
        Parameters = Array.AsReadOnly(parameters.ToArray());
        Groups = Array.AsReadOnly(groups.Select(CommandContractValidation.SnapshotGroup).ToArray());
        ParameterSets = Array.AsReadOnly(parameterSets.Select(s => s with { Parameters = Array.AsReadOnly(s.Parameters.ToArray()) }).ToArray());
        FlattenedParameters = Array.AsReadOnly(flattenedParameters.ToArray());
        this.binderFactory = binderFactory ?? throw new ArgumentNullException(nameof(binderFactory));
        this.validators = Array.AsReadOnly((validators ?? Array.Empty<ICommandArgumentValidator<TArguments>>()).ToArray());
    }

    public string Name { get; }

    public IReadOnlyList<string> Path { get; }

    public string RootName => Path[0];

    public string? Category { get; }

    public string Usage { get; }

    public IReadOnlyList<CommandParameterDeclaration> Parameters { get; }

    public IReadOnlyList<CommandGroupDeclaration> Groups { get; }

    public IReadOnlyList<CommandParameterSetSpec> ParameterSets { get; }

    public IReadOnlyList<CommandParameterDeclaration> FlattenedParameters { get; }

    public Func<BindingResolutionContext, Func<BindingSlotReader, TArguments>> BinderFactory => binderFactory;

    public IReadOnlyList<ICommandArgumentValidator<TArguments>> Validators => validators;

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Fluent definition builder entry")]
    public static CommandDefinitionBuilder<TArguments> Create(
        string name,
        string? category = null,
        string? usage = null) =>
        new(name, category, usage);
}

public sealed record CommandParameterDeclaration(
    string Name,
    string MemberName,
    int Order,
    bool Primary,
    bool Required,
    Type ValueType,
    bool IsCommandValue,
    string ValueHint,
    bool HasDefault = false,
    string? DefaultText = null,
    object? DefaultValue = null,
    double? Minimum = null,
    Type? ConverterType = null,
    string? GroupPath = null,
    Func<ICommandValueConverter>? ConverterFactory = null);

public sealed record CommandGroupDeclaration(
    string Name,
    string MemberName,
    int Order,
    Type GroupType,
    Type? ConfigurationType = null,
    Func<ICommandGroupConfiguration>? ConfigurationFactory = null,
    IReadOnlyList<CommandParameterDeclaration>? Parameters = null,
    IReadOnlyList<CommandGroupDeclaration>? SubGroups = null,
    IReadOnlyList<CommandParameterSetSpec>? ParameterSets = null);

public interface ICommandGroupConfiguration
{
    void Configure(CommandGroupConfigurationContext context);
}

public sealed class CommandGroupConfigurationContext
{
    private readonly Dictionary<string, string> defaultTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> orders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> valueHints = new(StringComparer.OrdinalIgnoreCase);

    public CommandGroupConfigurationContext SetDefaultText(string memberName, string defaultText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        defaultTexts[memberName] = defaultText;
        return this;
    }

    public CommandGroupConfigurationContext SetOrder(string memberName, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        orders[memberName] = order;
        return this;
    }

    public CommandGroupConfigurationContext SetValueHint(string memberName, string hint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        valueHints[memberName] = hint;
        return this;
    }

    internal void ValidateMembers(IEnumerable<string> members)
    {
        var known = new HashSet<string>(members, StringComparer.Ordinal);
        foreach (var member in defaultTexts.Keys.Concat(orders.Keys).Concat(valueHints.Keys))
            if (!known.Contains(member))
                throw new ArgumentException($"Group configuration references unknown member '{member}'.");
    }

    public bool TryGetDefaultText(string memberName, out string? text) =>
        defaultTexts.TryGetValue(memberName, out text);

    public bool TryGetOrder(string memberName, out int order) =>
        orders.TryGetValue(memberName, out order);

    public bool TryGetValueHint(string memberName, out string? hint) =>
        valueHints.TryGetValue(memberName, out hint);
}

public interface ICommandArgumentValidator<in TArguments>
{
    void Validate(TArguments arguments, CommandValidationContext context);
}

public sealed class CommandValidationContext
{
    public CommandValidationContext(string commandName, string usage)
    {
        CommandName = commandName;
        Usage = usage;
    }

    public string CommandName { get; }

    public string Usage { get; }

    public void AddError(string message) =>
        throw new RuntimeCommandException(
            CommandName,
            $"{message}. Usage: {Usage}.",
            phase: CommandFailurePhase.Binding);
}
