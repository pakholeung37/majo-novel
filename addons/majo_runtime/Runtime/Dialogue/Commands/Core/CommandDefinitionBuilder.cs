#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CommandDefinitionBuilder<TArguments>
    where TArguments : class
{
    private readonly string name;
    private readonly IReadOnlyList<string> path;
    private readonly string? category;
    private readonly string? explicitUsage;
    private readonly List<CommandParameterDeclaration> parameters = new();
    private readonly List<CommandGroupDeclaration> groups = new();
    private readonly List<CommandParameterSetSpec> parameterSets = new();
    private readonly List<ICommandArgumentValidator<TArguments>> validators = new();
    private int nextOrder;

    public CommandDefinitionBuilder(string name, string? category = null, string? usage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
        path = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        this.category = category;
        explicitUsage = usage;
    }

    public CommandDefinitionBuilder<TArguments> Primary<T>(
        string name,
        string? memberName = null,
        string? valueHint = null,
        double? minimum = null,
        Type? converter = null,
        Func<ICommandValueConverter>? converterFactory = null,
        int? order = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        parameters.Add(new CommandParameterDeclaration(
            Name: name,
            MemberName: memberName ?? name,
            Order: order ?? nextOrder++,
            Primary: true,
            Required: true,
            ValueType: typeof(T),
            IsCommandValue: false,
            ValueHint: valueHint ?? "value",
            Minimum: minimum,
            ConverterType: converter,
            ConverterFactory: converterFactory));
        return this;
    }

    public CommandDefinitionBuilder<TArguments> Required<T>(
        string name,
        string? memberName = null,
        string? valueHint = null,
        double? minimum = null,
        Type? converter = null,
        Func<ICommandValueConverter>? converterFactory = null,
        int? order = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        parameters.Add(new CommandParameterDeclaration(
            Name: name,
            MemberName: memberName ?? name,
            Order: order ?? nextOrder++,
            Primary: false,
            Required: true,
            ValueType: typeof(T),
            IsCommandValue: false,
            ValueHint: valueHint ?? "value",
            Minimum: minimum,
            ConverterType: converter,
            ConverterFactory: converterFactory));
        return this;
    }

    public CommandDefinitionBuilder<TArguments> Optional<T>(
        string name,
        string? memberName = null,
        string? valueHint = null,
        string? defaultText = null,
        object? defaultValue = null,
        double? minimum = null,
        Type? converter = null,
        Func<ICommandValueConverter>? converterFactory = null,
        int? order = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        parameters.Add(new CommandParameterDeclaration(
            Name: name,
            MemberName: memberName ?? name,
            Order: order ?? nextOrder++,
            Primary: false,
            Required: false,
            ValueType: typeof(T),
            IsCommandValue: true,
            ValueHint: valueHint ?? "value",
            HasDefault: defaultText is not null || defaultValue is not null,
            DefaultText: defaultText,
            DefaultValue: defaultValue,
            Minimum: minimum,
            ConverterType: converter,
            ConverterFactory: converterFactory));
        return this;
    }

    public CommandDefinitionBuilder<TArguments> Group(
        string name,
        string? memberName,
        Type groupType,
        Type? configuration = null,
        Func<ICommandGroupConfiguration>? configurationFactory = null,
        IReadOnlyList<CommandParameterDeclaration>? groupParameters = null,
        IReadOnlyList<CommandGroupDeclaration>? subGroups = null,
        IReadOnlyList<CommandParameterSetSpec>? parameterSets = null,
        int? order = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(groupType);
        groups.Add(new CommandGroupDeclaration(
            Name: name,
            MemberName: memberName ?? name,
            Order: order ?? nextOrder++,
            GroupType: groupType,
            ConfigurationType: configuration,
            ConfigurationFactory: configurationFactory,
            Parameters: groupParameters ?? throw new ArgumentException("Explicit group parameters are required.", nameof(groupParameters)),
            SubGroups: subGroups,
            ParameterSets: parameterSets));
        return this;
    }

    public CommandDefinitionBuilder<TArguments> Group(CommandGroupDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        groups.Add(CommandContractValidation.SnapshotGroup(declaration));
        return this;
    }

    public CommandDefinitionBuilder<TArguments> Group<TGroup>(
        string name,
        string? memberName = null,
        IReadOnlyList<CommandParameterDeclaration>? groupParameters = null,
        IReadOnlyList<CommandGroupDeclaration>? subGroups = null)
        where TGroup : class =>
        Group(name, memberName, typeof(TGroup), null, null, groupParameters, subGroups);

    public CommandDefinitionBuilder<TArguments> Group<TGroup, TConfiguration>(
        string name,
        string? memberName = null,
        IReadOnlyList<CommandParameterDeclaration>? groupParameters = null,
        IReadOnlyList<CommandGroupDeclaration>? subGroups = null)
        where TGroup : class
        where TConfiguration : class, ICommandGroupConfiguration, new() =>
        Group(name, memberName, typeof(TGroup), typeof(TConfiguration), () => new TConfiguration(), groupParameters, subGroups);

    public CommandDefinitionBuilder<TArguments> ParameterSet(
        CommandParameterSetRule rule,
        params string[] parameters)
    {
        if (parameters is null || parameters.Length < 2)
        {
            throw new ArgumentException("Parameter set requires at least two parameters.", nameof(parameters));
        }

        parameterSets.Add(new CommandParameterSetSpec(
            rule,
            Array.AsReadOnly(parameters.ToArray())));
        return this;
    }

    public CommandDefinitionBuilder<TArguments> Validator(ICommandArgumentValidator<TArguments> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        validators.Add(validator);
        return this;
    }

    public CommandDefinition<TArguments> Build(
        Func<BindingResolutionContext, Func<BindingSlotReader, TArguments>> binderFactory)
    {
        ArgumentNullException.ThrowIfNull(binderFactory);

        var duplicateParameter = parameters
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateParameter is not null)
        {
            throw new RuntimeCommandException(
                name,
                $"Parameter '{duplicateParameter.Key}' is declared more than once.",
                phase: CommandFailurePhase.Schema);
        }

        if (parameters.Count(p => p.Primary) > 1)
        {
            throw new RuntimeCommandException(
                name,
                "A command can declare at most one primary parameter.",
                phase: CommandFailurePhase.Schema);
        }

        // Flatten parameters (parameters + group parameters)
        var flattened = new List<CommandParameterDeclaration>(parameters);
        var existingNames = new HashSet<string>(parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        var effectiveSets = new List<CommandParameterSetSpec>(parameterSets);
        FlattenGroups(groups, flattened, existingNames, new HashSet<Type>(), effectiveSets, "");
        CommandContractValidation.ValidateParameters(name, flattened);

        var allNames = new HashSet<string>(flattened.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        allNames.UnionWith(flattened.Select(CommandContractValidation.MemberPath));

        foreach (var set in effectiveSets)
        {
            foreach (var param in set.Parameters)
            {
                if (!allNames.Contains(param))
                {
                    throw new RuntimeCommandException(
                        name,
                        $"Parameter set references unknown parameter '{param}'.",
                        phase: CommandFailurePhase.Schema);
                }
            }
        }

        var orderedFlattened = flattened.OrderBy(p => p.Order).ToArray();
        var usage = explicitUsage ?? BuildUsage(name, orderedFlattened);

        return new CommandDefinition<TArguments>(
            name,
            path,
            category,
            usage,
            new ReadOnlyCollection<CommandParameterDeclaration>(parameters),
            new ReadOnlyCollection<CommandGroupDeclaration>(groups),
            new ReadOnlyCollection<CommandParameterSetSpec>(effectiveSets.ToArray()),
            new ReadOnlyCollection<CommandParameterDeclaration>(orderedFlattened),
            binderFactory,
            new ReadOnlyCollection<ICommandArgumentValidator<TArguments>>(validators));
    }

    private void FlattenGroups(
        IEnumerable<CommandGroupDeclaration> groupList,
        List<CommandParameterDeclaration> target,
        HashSet<string> existingNames,
        HashSet<Type> visitedGroupTypes,
        List<CommandParameterSetSpec> effectiveSets,
        string parentPath)
    {
        foreach (var group in groupList)
        {
            if (!visitedGroupTypes.Add(group.GroupType))
            {
                throw new RuntimeCommandException(
                    name,
                    $"Cyclic parameter group reference detected for group type '{group.GroupType.FullName}'.",
                    phase: CommandFailurePhase.Schema);
            }

            CommandGroupConfigurationContext? configContext = null;
            if (group.ConfigurationFactory is not null)
            {
                var config = group.ConfigurationFactory();
                configContext = new CommandGroupConfigurationContext();
                config.Configure(configContext);
            }
            else if (group.ConfigurationType is not null)
            {
                throw new ArgumentException("Group configuration requires an explicit factory.");
            }

            var groupPath = string.IsNullOrEmpty(parentPath) ? group.MemberName : $"{parentPath}.{group.MemberName}";
            var groupParams = group.Parameters ?? throw new ArgumentException("Explicit group parameters are required.");
            configContext?.ValidateMembers(groupParams.Select(p => p.MemberName));
            foreach (var set in group.ParameterSets ?? Array.Empty<CommandParameterSetSpec>())
                effectiveSets.Add(set with { Parameters = Array.AsReadOnly(set.Parameters.Select(m => $"{groupPath}.{m}").ToArray()) });
            if (groupParams is not null)
            {
                foreach (var param in groupParams)
                {
                    var paramName = param.Name;
                    var paramOrder = param.Order;
                    var paramHint = param.ValueHint;
                    var paramDefaultText = param.DefaultText;
                    var hasDefault = param.HasDefault;

                    if (configContext is not null)
                    {
                        if (configContext.TryGetOrder(param.MemberName, out var overriddenOrder))
                        {
                            paramOrder = overriddenOrder;
                        }

                        if (configContext.TryGetValueHint(param.MemberName, out var overriddenHint) && overriddenHint is not null)
                        {
                            paramHint = overriddenHint;
                        }

                        if (configContext.TryGetDefaultText(param.MemberName, out var overriddenDefault))
                        {
                            paramDefaultText = overriddenDefault;
                            hasDefault = overriddenDefault is not null;
                        }
                    }

                    var effectiveParam = param with
                    {
                        Order = paramOrder,
                        ValueHint = paramHint,
                        DefaultText = paramDefaultText,
                        HasDefault = hasDefault,
                        GroupPath = groupPath,
                        DefaultValue = paramDefaultText != param.DefaultText ? null : param.DefaultValue,
                    };

                    if (!existingNames.Add(effectiveParam.Name))
                    {
                        throw new RuntimeCommandException(
                            name,
                            $"Parameter '{effectiveParam.Name}' from group '{group.Name}' conflicts with existing parameter.",
                            phase: CommandFailurePhase.Schema);
                    }

                    target.Add(effectiveParam);
                }
            }

            if (group.SubGroups is not null)
            {
                FlattenGroups(group.SubGroups, target, existingNames, visitedGroupTypes, effectiveSets, groupPath);
            }

            visitedGroupTypes.Remove(group.GroupType);
        }
    }

    private static string BuildUsage(string commandName, IReadOnlyList<CommandParameterDeclaration> parameters)
    {
        var parts = new List<string> { commandName };
        foreach (var param in parameters.OrderBy(p => p.Order))
        {
            if (param.Primary)
            {
                parts.Add(param.Required ? $"<{param.Name}>" : $"[{param.Name}]");
            }
            else
            {
                parts.Add(param.Required
                    ? $"<{param.Name}:{param.ValueHint}>"
                    : $"[{param.Name}:{param.ValueHint}]");
            }
        }

        return string.Join(' ', parts);
    }

}
