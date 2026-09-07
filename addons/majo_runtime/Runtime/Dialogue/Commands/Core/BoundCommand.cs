#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RawCommandArguments
{
    public RawCommandArguments(string? primary, IReadOnlyDictionary<string, string>? named)
    {
        Primary = primary;
        Named = named ?? new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public string? Primary { get; }

    public IReadOnlyDictionary<string, string> Named { get; }
}

public interface IBoundCommand : IRuntimeCommand
{
    IRuntimeCommand Command { get; }

    IReadOnlyList<BoundParameterSlot> Slots { get; }

    object BindUntyped(RawCommandArguments raw);

    object BindUntyped(CommandInput input);
}

public sealed class BoundParameterSlot
{
    public BoundParameterSlot(
        int slotIndex,
        string scriptName,
        CommandParameterDeclaration declaration,
        ICommandValueConverter converter,
        ICommandValueDescriptor? descriptor,
        bool hasContractDefault,
        object? contractDefaultValue,
        double? minimum)
    {
        SlotIndex = slotIndex;
        ScriptName = scriptName;
        Declaration = declaration;
        Converter = converter;
        Descriptor = descriptor;
        HasContractDefault = hasContractDefault;
        ContractDefaultValue = contractDefaultValue;
        Minimum = minimum;
        Description = descriptor?.Description?.Snapshot();
        FormattedDefault = hasContractDefault
            ? descriptor?.FormatUntyped(contractDefaultValue) ?? declaration.DefaultText
            : null;
    }

    public CommandValueDescription? Description { get; }

    public string? FormattedDefault { get; }

    public int SlotIndex { get; }

    public string ScriptName { get; }

    public CommandParameterDeclaration Declaration { get; }

    public ICommandValueConverter Converter { get; }

    public ICommandValueDescriptor? Descriptor { get; }

    public bool HasContractDefault { get; }

    public object? ContractDefaultValue { get; }

    public double? Minimum { get; }
}

public sealed class BindingResolutionContext
{
    private readonly Dictionary<string, int> slotIndicesByParameterName;
    private readonly IReadOnlyList<BoundParameterSlot> slots;
    private readonly IReadOnlyDictionary<string, int> memberSlots;

    public BindingResolutionContext(
        Dictionary<string, int> slotIndicesByParameterName,
        IReadOnlyList<BoundParameterSlot> slots,
        IReadOnlyDictionary<string, int> memberSlots)
    {
        this.slotIndicesByParameterName = slotIndicesByParameterName;
        this.slots = slots;
        this.memberSlots = memberSlots;
    }

    public int GetSlot(string parameterNameOrPath)
    {
        if (slotIndicesByParameterName.TryGetValue(parameterNameOrPath, out var index))
        {
            return index;
        }

        if (parameterNameOrPath.Contains('.') && memberSlots.TryGetValue(parameterNameOrPath, out index)) return index;

        throw new ArgumentException(
            $"Parameter '{parameterNameOrPath}' was not found in binding resolution context.",
            nameof(parameterNameOrPath));
    }

    public int GetMemberSlot(string memberPath) => memberSlots.TryGetValue(memberPath, out var slot)
        ? slot : throw new ArgumentException($"Unknown parameter member path '{memberPath}'.");

    public BoundParameterSlot GetSlot(int slotIndex) => slots[slotIndex];
}

public sealed class BindingSlotReader
{
    private readonly object?[] values;
    private readonly bool[] wasProvided;
    private readonly CommandValueOrigin[] origins;

    public BindingSlotReader(int slotCount)
    {
        values = new object?[slotCount];
        wasProvided = new bool[slotCount];
        origins = new CommandValueOrigin[slotCount];
    }

    public void Set(int slotIndex, object? value, bool provided, CommandValueOrigin origin)
    {
        values[slotIndex] = value;
        wasProvided[slotIndex] = provided;
        origins[slotIndex] = origin;
    }

    public T ReadRequired<T>(int slotIndex)
    {
        var val = values[slotIndex];
        if (val is null)
        {
            throw new InvalidOperationException($"Required slot {slotIndex} value was null.");
        }

        return (T)val;
    }

    public CommandValue<T> ReadOptional<T>(int slotIndex)
    {
        if (origins[slotIndex] == CommandValueOrigin.Unspecified)
            return CommandValue<T>.Unspecified();
        if (values[slotIndex] is not T typed)
            throw new InvalidOperationException($"Slot {slotIndex} does not contain '{typeof(T).FullName}'.");
        return new CommandValue<T>(wasProvided[slotIndex], typed, origins[slotIndex]);
    }


    public object? ReadValue(int slotIndex) => values[slotIndex];

    public bool WasProvided(int slotIndex) => wasProvided[slotIndex];

    public CommandValueOrigin GetOrigin(int slotIndex) => origins[slotIndex];
}

public sealed class BoundCommand<TArguments> : IBoundCommand
    where TArguments : class
{
    private readonly RuntimeCommand<TArguments> command;
    private readonly CommandDefinition<TArguments> definition;
    private readonly Func<BindingSlotReader, TArguments> argumentsFactory;
    private readonly BoundParameterSlot[] slots;
    private readonly BoundParameterSlot? primarySlot;
    private readonly Dictionary<string, BoundParameterSlot> namedSlots;
    private readonly IReadOnlyList<CommandParameterSetSpec> parameterSets;
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<ICommandArgumentValidator<TArguments>> validators;

    public BoundCommand(
        RuntimeCommand<TArguments> command,
        CommandDefinition<TArguments> definition,
        Func<BindingSlotReader, TArguments> argumentsFactory,
        BoundParameterSlot[] slots,
        IReadOnlyList<CommandParameterSetSpec> parameterSets,
        IReadOnlyList<ICommandArgumentValidator<TArguments>> validators)
    {
        this.command = command ?? throw new ArgumentNullException(nameof(command));
        this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
        this.argumentsFactory = argumentsFactory ?? throw new ArgumentNullException(nameof(argumentsFactory));
        this.slots = (slots ?? throw new ArgumentNullException(nameof(slots))).ToArray();
        this.parameterSets = Array.AsReadOnly((parameterSets ?? Array.Empty<CommandParameterSetSpec>()).Select(s => s with { Parameters = Array.AsReadOnly(s.Parameters.ToArray()) }).ToArray());
        this.validators = Array.AsReadOnly((validators ?? Array.Empty<ICommandArgumentValidator<TArguments>>()).ToArray());

        namedSlots = new Dictionary<string, BoundParameterSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            if (slot.Declaration.Primary)
            {
                primarySlot = slot;
            }
            else
            {
                namedSlots[slot.ScriptName] = slot;
            }
        }
    }

    public RuntimeCommand<TArguments> Command => command;

    IRuntimeCommand IBoundCommand.Command => command;

    public CommandDefinition<TArguments> Definition => definition;

    ICommandDefinition IRuntimeCommand.Definition => definition;

    IBoundCommand IRuntimeCommand.CreateBoundCommand(
        BindingResolutionContext resolutionContext,
        BoundParameterSlot[] slots) => this;

    public string Name => definition.Name;

    public string RootName => definition.RootName;

    public IReadOnlyList<string> Path => definition.Path;

    public string Usage => Name + (slots.Length == 0 ? "" : " " + string.Join(" ", slots.Select(slot =>
    {
        var hint = slot.Description?.Syntax ?? (slot.Description?.CanonicalValues is { Count: > 0 } values
            ? string.Join("|", values) : slot.Declaration.ValueHint);
        var value = slot.Declaration.Primary ? slot.ScriptName : $"{slot.ScriptName}:{hint}";
        return slot.Declaration.Required ? $"<{value}>" : $"[{value}]";
    })));

    public IReadOnlyList<BoundParameterSlot> Slots => Array.AsReadOnly(slots);

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

        var raw = ParseRaw(input.RawArguments);
        return Bind(raw);
    }

    public TArguments Bind(RawCommandArguments raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        ValidateParameterSets(raw);

        var reader = new BindingSlotReader(slots.Length);

        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            bool wasProvided = false;
            string? rawText = null;

            if (slot.Declaration.Primary)
            {
                wasProvided = raw.Primary is not null;
                rawText = raw.Primary;
            }
            else
            {
                if (raw.Named.TryGetValue(slot.ScriptName, out rawText))
                {
                    wasProvided = true;
                }
                else
                {
                    foreach (var kvp in raw.Named)
                    {
                        if (string.Equals(kvp.Key, slot.ScriptName, StringComparison.OrdinalIgnoreCase))
                        {
                            wasProvided = true;
                            rawText = kvp.Value;
                            break;
                        }
                    }
                }
            }

            if (wasProvided)
            {
                if (string.IsNullOrWhiteSpace(rawText) || rawText == "-")
                {
                    throw new RuntimeCommandException(
                        Name,
                        $"parameter '{slot.ScriptName}' requires a value. Usage: {Usage}.",
                        phase: CommandFailurePhase.Binding);
                }

                object converted;
                try
                {
                    converted = slot.Converter.ParseUntyped(rawText)!;
                }
                catch (FormatException ex)
                {
                    throw new RuntimeCommandException(
                        Name,
                        $"{slot.ScriptName} '{rawText}' {ex.Message}. Usage: {Usage}.",
                        ex,
                        phase: CommandFailurePhase.Binding);
                }
                catch (Exception ex)
                {
                    throw new RuntimeCommandException(
                        Name,
                        ex.Message,
                        ex,
                        phase: CommandFailurePhase.Binding);
                }

                CommandContractValidation.ValidateValue(Name, slot.Declaration, converted, CommandFailurePhase.Binding);

                if (slot.Minimum.HasValue && converted is IConvertible convertible)
                {
                    var num = convertible.ToDouble(CultureInfo.InvariantCulture);
                    if (num < slot.Minimum.Value)
                    {
                        throw new RuntimeCommandException(
                            Name,
                            $"{slot.ScriptName} '{rawText}' must be at least {slot.Minimum.Value.ToString(CultureInfo.InvariantCulture)}. Usage: {Usage}.",
                            phase: CommandFailurePhase.Binding);
                    }
                }

                reader.Set(i, converted, provided: true, CommandValueOrigin.Explicit);
            }
            else
            {
                if (slot.Declaration.Required)
                {
                    throw new RuntimeCommandException(
                        Name,
                        slot.Declaration.Primary
                            ? $"expected primary parameter '{slot.ScriptName}'. Usage: {Usage}."
                            : $"parameter '{slot.ScriptName}' is required. Usage: {Usage}.",
                        phase: CommandFailurePhase.Binding);
                }

                if (slot.HasContractDefault)
                {
                    reader.Set(i, slot.ContractDefaultValue, provided: false, CommandValueOrigin.ContractDefault);
                }
                else
                {
                    reader.Set(i, default, provided: false, CommandValueOrigin.Unspecified);
                }
            }
        }

        var arguments = argumentsFactory(reader);

        if (validators.Count > 0)
        {
            var validationContext = new CommandValidationContext(Name, Usage);
            foreach (var validator in validators)
            {
                validator.Validate(arguments, validationContext);
            }
        }

        return arguments;
    }

    public object BindUntyped(RawCommandArguments raw) => Bind(raw);

    public object BindUntyped(CommandInput input) => Bind(input);

    public async ValueTask ExecuteAsync(CommandInput input, CommandExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        TArguments arguments;
        try
        {
            arguments = Bind(input);
        }
        catch (RuntimeCommandException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RuntimeCommandException(
                Name,
                exception.Message,
                exception,
                recoverable: true,
                phase: CommandFailurePhase.Binding);
        }

        try
        {
            await command.InvokeExecuteAsync(arguments, context);
        }
        catch (RuntimeCommandException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RuntimeCommandException(
                Name,
                exception.Message,
                exception,
                recoverable: false,
                phase: CommandFailurePhase.Execution);
        }
    }

    public RawCommandArguments ParseRaw(IReadOnlyList<string> arguments)
    {
        string? primary = null;
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf(':');
            if (separator < 0)
            {
                if (primarySlot is null || primary is not null)
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

            if (!namedSlots.TryGetValue(name, out var slot) || slot.Declaration.Primary)
            {
                throw new RuntimeCommandException(
                    Name,
                    $"unknown parameter '{name}'. Usage: {Usage}.",
                    phase: CommandFailurePhase.Binding);
            }

            if (!named.TryAdd(slot.ScriptName, value))
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
        if (raw.Primary is not null && primarySlot is not null)
        {
            assignedNames.Add(primarySlot.ScriptName);
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
}
