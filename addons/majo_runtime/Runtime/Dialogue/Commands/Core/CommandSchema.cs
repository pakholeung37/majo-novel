#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RawCommandArguments
{
    public RawCommandArguments(string? primary, IReadOnlyDictionary<string, string> named)
    {
        Primary = primary;
        Named = named ?? new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public string? Primary { get; }

    public IReadOnlyDictionary<string, string> Named { get; }
}

public sealed class CommandArgumentValues
{
    private readonly string commandName;
    private readonly string usage;
    private readonly Dictionary<string, object> values;
    private readonly RawCommandArguments rawArguments;
    private readonly CommandBindingEnvironment environment;
    private readonly IReadOnlyDictionary<string, ICommandParameterGroup> groups;

    internal CommandArgumentValues(
        string commandName,
        string usage,
        Dictionary<string, object> values,
        RawCommandArguments rawArguments,
        CommandBindingEnvironment environment,
        IReadOnlyDictionary<string, ICommandParameterGroup> groups)
    {
        this.commandName = commandName;
        this.usage = usage;
        this.values = values;
        this.rawArguments = rawArguments;
        this.environment = environment;
        this.groups = groups;
    }

    public T Required<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!values.TryGetValue(name, out var boxed))
        {
            throw new RuntimeCommandException(
                commandName,
                $"parameter '{name}' is required. Usage: {usage}.",
                phase: CommandFailurePhase.Binding);
        }

        if (boxed is CommandValue<T> presenceValue)
        {
            if (!presenceValue.HasValue || presenceValue.Value is null)
            {
                throw new RuntimeCommandException(
                    commandName,
                    $"parameter '{name}' is required. Usage: {usage}.",
                    phase: CommandFailurePhase.Binding);
            }

            return presenceValue.Value;
        }

        if (boxed is T directValue)
        {
            return directValue;
        }

        throw new RuntimeCommandException(
            commandName,
            $"Parameter '{name}' expected type '{typeof(T).FullName}' but was '{boxed.GetType().FullName}'.",
            phase: CommandFailurePhase.Binding);
    }

    public CommandValue<T> Optional<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!values.TryGetValue(name, out var boxed))
        {
            return CommandValue<T>.Unspecified();
        }

        if (boxed is CommandValue<T> typed)
        {
            return typed;
        }

        if (boxed is T direct)
        {
            return CommandValue<T>.Explicit(direct);
        }

        throw new RuntimeCommandException(
            commandName,
            $"Parameter '{name}' expected type '{typeof(T).FullName}' but was '{boxed.GetType().FullName}'.",
            phase: CommandFailurePhase.Binding);
    }

    public TGroup Group<TGroup>(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (!groups.TryGetValue(groupId, out var group))
        {
            throw new RuntimeCommandException(
                commandName,
                $"Parameter group '{groupId}' is not registered in schema.",
                phase: CommandFailurePhase.Binding);
        }

        if (group is ICommandParameterGroup<TGroup> typedGroup)
        {
            try
            {
                return typedGroup.Bind(rawArguments, environment);
            }
            catch (RuntimeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RuntimeCommandException(
                    commandName,
                    $"{exception.Message}. Usage: {usage}.",
                    exception,
                    phase: CommandFailurePhase.Binding);
            }
        }

        throw new RuntimeCommandException(
            commandName,
            $"Parameter group '{groupId}' does not produce type '{typeof(TGroup).FullName}'.",
            phase: CommandFailurePhase.Binding);
    }
}

public interface ICommandSchema
{
    string Name { get; }

    IReadOnlyList<string> Path { get; }

    string RootName { get; }

    string? Category { get; }

    string Usage { get; }

    IReadOnlyList<CommandParameterSpec> Parameters { get; }

    IReadOnlyList<CommandParameterSetSpec> ParameterSets { get; }

    IReadOnlyDictionary<string, ICommandParameterGroup> Groups { get; }

    bool IsEnvironmentAttached { get; }

    void Validate(CommandBindingEnvironment environment);

    void AttachEnvironment(CommandBindingEnvironment environment);
}

[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Fluent schema builder factory")]
public sealed class CommandSchema<TArguments> : ICommandSchema
    where TArguments : class
{
    private readonly Func<CommandArgumentValues, TArguments> factory;
    private readonly IReadOnlyList<CommandParameterSpec> commandParameters;
    private readonly IReadOnlyList<CommandParameterSpec> allParameters;
    private readonly IReadOnlyList<CommandParameterSetSpec> parameterSets;
    private readonly IReadOnlyDictionary<string, ICommandParameterGroup> groups;
    private CommandBindingEnvironment? attachedEnvironment;

    internal CommandSchema(
        string name,
        IReadOnlyList<string> path,
        string? category,
        string usage,
        IReadOnlyList<CommandParameterSpec> commandParameters,
        IReadOnlyList<CommandParameterSpec> allParameters,
        IReadOnlyList<CommandParameterSetSpec> parameterSets,
        IReadOnlyDictionary<string, ICommandParameterGroup> groups,
        Func<CommandArgumentValues, TArguments> factory)
    {
        Name = name;
        Path = path;
        Category = category;
        Usage = usage;
        this.commandParameters = commandParameters;
        this.allParameters = allParameters;
        this.parameterSets = parameterSets;
        this.groups = groups;
        this.factory = factory;
    }

    public string Name { get; }

    public IReadOnlyList<string> Path { get; }

    public string RootName => Path[0];

    public string? Category { get; }

    public string Usage { get; }

    public IReadOnlyList<CommandParameterSpec> Parameters => allParameters;

    public IReadOnlyList<CommandParameterSetSpec> ParameterSets => parameterSets;

    public IReadOnlyDictionary<string, ICommandParameterGroup> Groups => groups;

    public bool IsEnvironmentAttached => attachedEnvironment is not null;

    public static CommandSchemaBuilder<TArguments> Create(
        string name,
        string? category = null,
        string? usage = null)
    {
        return new CommandSchemaBuilder<TArguments>(name, category, usage);
    }

    public void Validate(CommandBindingEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var parameter in allParameters)
        {
            if (!environment.Converters.CanConvert(parameter.ValueType))
            {
                throw new RuntimeCommandException(
                    Name,
                    $"No command value converter is registered for '{parameter.ValueType.FullName}' parameter '{parameter.Name}'.",
                    phase: CommandFailurePhase.Schema);
            }

            if (parameter.ContractDefault is not null &&
                !parameter.ValueType.IsInstanceOfType(parameter.ContractDefault))
            {
                throw new RuntimeCommandException(
                    Name,
                    $"Contract default for parameter '{parameter.Name}' is not of type '{parameter.ValueType.FullName}'.",
                    phase: CommandFailurePhase.Schema);
            }
        }

        var compositionContext = new CommandSchemaCompositionContext(
            Name,
            new HashSet<string>(commandParameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase));

        foreach (var group in groups.Values)
        {
            group.Validate(compositionContext);
        }
    }

    public void AttachEnvironment(CommandBindingEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (attachedEnvironment is not null && !ReferenceEquals(attachedEnvironment, environment))
        {
            throw new InvalidOperationException($"Command schema '{Name}' is already attached to a binding environment.");
        }
        attachedEnvironment = environment;
    }

    public TArguments Bind(RawCommandArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var env = attachedEnvironment ?? CommandBindingEnvironment.Default;
        return BindInternal(arguments, env);
    }

    public TArguments Bind(CommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!string.Equals(input.CommandName, Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeCommandException(
                Name,
                $"Command name mismatch: expected '{Name}', got '{input.CommandName}'.",
                phase: CommandFailurePhase.Binding);
        }

        var env = attachedEnvironment ?? CommandBindingEnvironment.Default;
        var raw = ParseRaw(input.RawArguments);
        return BindInternal(raw, env);
    }

    private TArguments BindInternal(RawCommandArguments raw, CommandBindingEnvironment env)
    {
        ValidateParameterSets(raw);

        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in allParameters)
        {
            var wasProvided = TryGetRawValue(parameter, raw, out var stringValue);

            if (!wasProvided)
            {
                if (parameter.Required)
                {
                    throw new RuntimeCommandException(
                        Name,
                        parameter.Primary
                            ? $"expected primary parameter '{parameter.Name}'. Usage: {Usage}."
                            : $"parameter '{parameter.Name}' is required. Usage: {Usage}.",
                        phase: CommandFailurePhase.Binding);
                }

                if (parameter.ContractDefault is not null)
                {
                    values[parameter.Name] = CreatePresenceValue(
                        parameter.ValueType,
                        false,
                        parameter.ContractDefault,
                        CommandValueOrigin.ContractDefault);
                }
                else
                {
                    values[parameter.Name] = CreatePresenceValue(
                        parameter.ValueType,
                        false,
                        null,
                        CommandValueOrigin.Unspecified);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(stringValue) || stringValue == "-")
            {
                throw new RuntimeCommandException(
                    Name,
                    $"parameter '{parameter.Name}' requires a value. Usage: {Usage}.",
                    phase: CommandFailurePhase.Binding);
            }

            object converted;
            try
            {
                converted = env.Converters.Convert(stringValue, parameter.ValueType);
            }
            catch (FormatException exception)
            {
                throw new RuntimeCommandException(
                    Name,
                    $"{parameter.Name} '{stringValue}' {exception.Message}. Usage: {Usage}.",
                    exception,
                    phase: CommandFailurePhase.Binding);
            }
            catch (InvalidOperationException exception)
            {
                throw new RuntimeCommandException(
                    Name,
                    exception.Message,
                    exception,
                    phase: CommandFailurePhase.Binding);
            }

            if (!double.IsNaN(parameter.Minimum) &&
                converted is IConvertible convertible &&
                convertible.ToDouble(CultureInfo.InvariantCulture) < parameter.Minimum)
            {
                throw new RuntimeCommandException(
                    Name,
                    $"{parameter.Name} '{stringValue}' must be at least {parameter.Minimum.ToString(CultureInfo.InvariantCulture)}. Usage: {Usage}.",
                    phase: CommandFailurePhase.Binding);
            }

            values[parameter.Name] = CreatePresenceValue(
                parameter.ValueType,
                true,
                converted,
                CommandValueOrigin.Explicit);
        }

        var argumentValues = new CommandArgumentValues(
            Name,
            Usage,
            values,
            raw,
            env,
            groups);

        return factory(argumentValues);
    }

    private RawCommandArguments ParseRaw(IReadOnlyList<string> arguments)
    {
        var primaryParameter = commandParameters.FirstOrDefault(p => p.Primary);
        string? primary = null;
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var knownParameters = new Dictionary<string, CommandParameterSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in allParameters)
        {
            knownParameters[p.Name] = p;
        }

        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf(':');
            if (separator < 0)
            {
                if (primaryParameter is null || primary is not null)
                {
                    throw new RuntimeCommandException(
                        Name,
                        $"unexpected positional argument '{argument}'. Usage: {Usage}.",
                        phase: CommandFailurePhase.Binding);
                }

                primary = argument;
                continue;
            }

            var name = argument[..separator];
            var value = argument[(separator + 1)..];

            if (!knownParameters.TryGetValue(name, out var spec) || spec.Primary)
            {
                throw new RuntimeCommandException(
                    Name,
                    $"unknown parameter '{name}'. Usage: {Usage}.",
                    phase: CommandFailurePhase.Binding);
            }

            if (!named.TryAdd(spec.Name, value))
            {
                throw new RuntimeCommandException(
                    Name,
                    $"parameter '{name}' is duplicated. Usage: {Usage}.",
                    phase: CommandFailurePhase.Binding);
            }
        }

        return new RawCommandArguments(primary, named);
    }

    private void ValidateParameterSets(RawCommandArguments raw)
    {
        var assignedNames = new HashSet<string>(raw.Named.Keys, StringComparer.OrdinalIgnoreCase);
        if (raw.Primary is not null)
        {
            var primaryParam = commandParameters.FirstOrDefault(p => p.Primary);
            if (primaryParam is not null)
            {
                assignedNames.Add(primaryParam.Name);
            }
        }

        foreach (var set in parameterSets)
        {
            var count = set.Parameters.Count(assignedNames.Contains);
            if ((set.Rule is CommandParameterSetRule.AtMostOne or CommandParameterSetRule.ExactlyOne) &&
                count > 1)
            {
                throw new RuntimeCommandException(
                    Name,
                    $"{string.Join(" and ", set.Parameters)} are mutually exclusive. Usage: {Usage}.",
                    phase: CommandFailurePhase.Binding);
            }

            if (set.Rule == CommandParameterSetRule.ExactlyOne && count != 1)
            {
                throw new RuntimeCommandException(
                    Name,
                    $"either {string.Join(" or ", set.Parameters)} is required. Usage: {Usage}.",
                    phase: CommandFailurePhase.Binding);
            }

            if (set.Rule == CommandParameterSetRule.AtLeastOne && count == 0)
            {
                throw new RuntimeCommandException(
                    Name,
                    $"at least one of {string.Join(", ", set.Parameters)} is required. Usage: {Usage}.",
                    phase: CommandFailurePhase.Binding);
            }
        }
    }

    private static bool TryGetRawValue(
        CommandParameterSpec parameter,
        RawCommandArguments arguments,
        out string? value)
    {
        if (parameter.Primary)
        {
            value = arguments.Primary;
            return arguments.Primary is not null;
        }

        return arguments.Named.TryGetValue(parameter.Name, out value);
    }

    private static object CreatePresenceValue(
        Type valueType,
        bool wasProvided,
        object? value,
        CommandValueOrigin origin)
    {
        var genericType = typeof(CommandValue<>).MakeGenericType(valueType);
        return Activator.CreateInstance(genericType, wasProvided, value, origin)!;
    }
}

public sealed class CommandSchemaBuilder<TArguments>
    where TArguments : class
{
    private readonly string name;
    private readonly IReadOnlyList<string> path;
    private readonly string? category;
    private string? usage;
    private readonly List<CommandParameterSpec> commandParameters = new();
    private readonly List<CommandParameterSetSpec> parameterSets = new();
    private readonly Dictionary<string, ICommandParameterGroup> groups =
        new(StringComparer.OrdinalIgnoreCase);
    private int nextOrder;

    internal CommandSchemaBuilder(string name, string? category, string? usage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.name = name;
        path = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        this.category = category;
        this.usage = usage;
    }

    public CommandSchemaBuilder<TArguments> Primary<T>(
        string name,
        string? valueHint = null,
        double minimum = double.NaN)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        commandParameters.Add(new CommandParameterSpec(
            name,
            nextOrder++,
            Primary: true,
            Required: true,
            typeof(T),
            valueHint ?? "value",
            ContractDefault: null,
            minimum));
        return this;
    }

    public CommandSchemaBuilder<TArguments> Required<T>(
        string name,
        string? valueHint = null,
        double minimum = double.NaN)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        commandParameters.Add(new CommandParameterSpec(
            name,
            nextOrder++,
            Primary: false,
            Required: true,
            typeof(T),
            valueHint ?? "value",
            ContractDefault: null,
            minimum));
        return this;
    }

    public CommandSchemaBuilder<TArguments> Optional<T>(
        string name,
        string? valueHint = null,
        object? contractDefault = null,
        bool hasContractDefault = false,
        double minimum = double.NaN)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        commandParameters.Add(new CommandParameterSpec(
            name,
            nextOrder++,
            Primary: false,
            Required: false,
            typeof(T),
            valueHint ?? "value",
            ContractDefault: (hasContractDefault || contractDefault is not null) ? contractDefault : null,
            minimum));
        return this;
    }

    public CommandSchemaBuilder<TArguments> Use(
        string groupId,
        ICommandParameterGroup group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(group);

        if (!groups.TryAdd(groupId, group))
        {
            throw new ArgumentException(
                $"Parameter group '{groupId}' is already registered.",
                nameof(groupId));
        }

        return this;
    }

    public CommandSchemaBuilder<TArguments> Use<TGroup>(
        string groupId,
        ICommandParameterGroup<TGroup> group) =>
        Use(groupId, (ICommandParameterGroup)group);

    public CommandSchemaBuilder<TArguments> Use(
        string groupId,
        CommandParameterGroupRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Use(groupId, registry.Get(groupId));
    }

    public CommandSchemaBuilder<TArguments> ParameterSet(
        CommandParameterSetRule rule,
        params string[] parameters)
    {
        if (parameters is null || parameters.Length < 2)
        {
            throw new ArgumentException(
                "Parameter set requires at least two parameters.",
                nameof(parameters));
        }

        parameterSets.Add(new CommandParameterSetSpec(
            rule,
            Array.AsReadOnly(parameters.ToArray())));
        return this;
    }

    public CommandSchema<TArguments> Build(
        Func<CommandArgumentValues, TArguments> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var duplicateCommandParameter = commandParameters
            .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCommandParameter is not null)
        {
            throw new RuntimeCommandException(
                name,
                $"Parameter '{duplicateCommandParameter.Key}' is declared more than once.",
                phase: CommandFailurePhase.Schema);
        }

        if (commandParameters.Count(parameter => parameter.Primary) > 1)
        {
            throw new RuntimeCommandException(
                name,
                "A command can declare at most one primary parameter.",
                phase: CommandFailurePhase.Schema);
        }

        var parameterNames = new HashSet<string>(
            commandParameters.Select(parameter => parameter.Name)
                .Concat(groups.Values.SelectMany(group => group.Parameters).Select(parameter => parameter.Name)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var parameterSet in parameterSets)
        {
            foreach (var parameterName in parameterSet.Parameters)
            {
                if (!parameterNames.Contains(parameterName))
                {
                    throw new RuntimeCommandException(
                        name,
                        $"Parameter set references unknown parameter '{parameterName}'.",
                        phase: CommandFailurePhase.Schema);
                }
            }
        }

        var allParameters = new List<CommandParameterSpec>(commandParameters);
        var existingNames = new HashSet<string>(
            commandParameters.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups.Values)
        {
            foreach (var groupParam in group.Parameters)
            {
                if (!existingNames.Add(groupParam.Name))
                {
                    throw new RuntimeCommandException(
                        name,
                        $"Parameter '{groupParam.Name}' from group '{group.Id}' conflicts with existing parameter.",
                        phase: CommandFailurePhase.Schema);
                }

                allParameters.Add(groupParam);
            }
        }

        var finalUsage = usage ?? BuildUsage(name, allParameters);

        return new CommandSchema<TArguments>(
            name,
            path,
            category,
            finalUsage,
            new ReadOnlyCollection<CommandParameterSpec>(commandParameters),
            new ReadOnlyCollection<CommandParameterSpec>(allParameters.OrderBy(p => p.Order).ToArray()),
            new ReadOnlyCollection<CommandParameterSetSpec>(parameterSets),
            new ReadOnlyDictionary<string, ICommandParameterGroup>(groups),
            factory);
    }

    private static string BuildUsage(string commandName, IReadOnlyList<CommandParameterSpec> parameters)
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
