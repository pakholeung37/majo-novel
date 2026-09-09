#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Commands;

public interface IRuntimeCommandRegistrar
{
    IRuntimeCommandRegistrar Add(
        string name,
        Func<IServiceResolver, IRuntimeCommand> factory,
        string providerId,
        string? version = null);
}

public interface IRuntimeCommandModule
{
    void Register(CommandRegistrationContext context);


}

public sealed class CommandFactoryRegistration
{
    public CommandFactoryRegistration(
        string name,
        Func<IServiceResolver, IRuntimeCommand> factory,
        string providerId,
        string? version)
    {
        Name = name;
        Factory = factory;
        ProviderId = providerId;
        Version = version;
    }

    public string Name { get; }

    public Func<IServiceResolver, IRuntimeCommand> Factory { get; }

    public string ProviderId { get; }

    public string? Version { get; }
}

public sealed class RuntimeCommandRegistry : IRuntimeCommandRegistrar
{
    private static readonly HashSet<string> YarnReservedNames = new(
        new[] { "wait", "stop" },
        StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, CommandFactoryRegistration> registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CommandConverterRegistry converters;
    private readonly CommandParameterGroupRegistry groups;
    private bool isRegistrationSealed;
    private bool isSealed;

    public RuntimeCommandRegistry(
        CommandConverterRegistry? converters = null,
        CommandParameterGroupRegistry? groups = null)
    {
        this.converters = converters ?? new CommandConverterRegistry();
        this.groups = groups ?? new CommandParameterGroupRegistry();
    }

    public CommandRegistrationContext Registration => new(this, converters, groups);

    public CommandConverterRegistry Converters => converters;

    public CommandParameterGroupRegistry Groups => groups;

    public bool IsSealed => isSealed;

    public IReadOnlyCollection<CommandFactoryRegistration> Registrations =>
        new ReadOnlyCollection<CommandFactoryRegistration>(registrations.Values.ToList());

    public IRuntimeCommandRegistrar Add(
        string name,
        Func<IServiceResolver, IRuntimeCommand> factory,
        string providerId,
        string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        EnsureMutable();

        var segments = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("Command name requires at least one segment.", nameof(name));
        }

        foreach (var segment in segments)
        {
            if (segment.Contains(':') || segment.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException(
                    "Command path segments cannot contain whitespace or colons.",
                    nameof(name));
            }
        }

        if (YarnReservedNames.Contains(segments[0]))
        {
            throw new RuntimeCommandException(
                name,
                "Command name is reserved by Yarn Spinner.",
                phase: CommandFailurePhase.Schema);
        }

        var normalizedName = string.Join(' ', segments);
        var entry = new CommandFactoryRegistration(normalizedName, factory, providerId, version);

        if (!registrations.TryAdd(normalizedName, entry))
        {
            throw new RuntimeCommandException(
                normalizedName,
                "Command is already registered.",
                phase: CommandFailurePhase.Schema);
        }

        return this;
    }

    public RuntimeCommandRegistry RegisterModule(IRuntimeCommandModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        EnsureMutable();

        var commandSnapshot = CreateSnapshot();
        var converterSnapshot = converters.CreateSnapshot();
        var groupSnapshot = groups.CreateSnapshot();

        try
        {
            var context = new CommandRegistrationContext(this, converters, groups);
            module.Register(context);
        }
        catch
        {
            RestoreSnapshot(commandSnapshot);
            converters.RestoreSnapshot(converterSnapshot);
            groups.RestoreSnapshot(groupSnapshot);
            throw;
        }

        return this;
    }

    internal CommandRegistrationSnapshot CreateSnapshot() =>
        new(new Dictionary<string, CommandFactoryRegistration>(registrations, StringComparer.OrdinalIgnoreCase));

    internal void RestoreSnapshot(CommandRegistrationSnapshot snapshot)
    {
        registrations.Clear();
        foreach (var (k, v) in snapshot.Registrations)
        {
            registrations[k] = v;
        }
    }

    internal sealed record CommandRegistrationSnapshot(
        Dictionary<string, CommandFactoryRegistration> Registrations);

    public void SealRegistrations()
    {
        isRegistrationSealed = true;
        converters.Seal();
        groups.Seal();
    }

    public RuntimeCommandCatalog BuildCatalog(IServiceResolver? services = null)
    {
        var resolver = services ?? EmptyServiceResolver.Instance;
        var resolvedConverters = converters.Resolve(resolver);

        var instantiatedCommands = new List<(CommandFactoryRegistration Reg, IRuntimeCommand Command)>(registrations.Count);

        // Step 1: Instantiate all commands and validate names
        foreach (var registration in registrations.Values)
        {
            IRuntimeCommand command;
            try
            {
                command = registration.Factory(resolver);
            }
            catch (Exception exception)
            {
                throw new RuntimeCommandException(
                    registration.Name,
                    $"Failed to create command '{registration.Name}': {exception.Message}",
                    exception,
                    recoverable: false,
                    phase: CommandFailurePhase.Schema);
            }

            if (command is null || command.Definition is null)
            {
                throw new RuntimeCommandException(
                    registration.Name,
                    $"Command factory for '{registration.Name}' returned null command or schema.",
                    recoverable: false,
                    phase: CommandFailurePhase.Schema);
            }

            if (!string.Equals(command.Definition.Name, registration.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeCommandException(
                    registration.Name,
                    $"Command schema name '{command.Definition.Name}' does not match registered name '{registration.Name}'.",
                    recoverable: false,
                    phase: CommandFailurePhase.Schema);
            }

            instantiatedCommands.Add((registration, command));
        }

        // Step 2: Build and validate BoundCommand for each command atomically
        var builtEntries = new List<RuntimeCommandCatalogEntry>(instantiatedCommands.Count);
        foreach (var (registration, command) in instantiatedCommands)
        {
            var def = command.Definition;
            var flattened = def.FlattenedParameters;
            CommandContractValidation.ValidateParameters(def.Name, flattened);
            _ = CommandContractValidation.ResolveSets(def);
            var slots = new BoundParameterSlot[flattened.Count];
            var slotIndicesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var memberSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < flattened.Count; i++)
            {
                var decl = flattened[i];
                var converter = resolvedConverters.ResolveForParameter(decl.ValueType, decl.ConverterType, def.Name, decl.Name, decl.ConverterFactory);
                var descriptor = converter as ICommandValueDescriptor;

                object? contractDefaultValue = null;
                var hasDefault = decl.HasDefault;

                if (hasDefault)
                {
                    if (decl.DefaultValue is not null)
                    {
                        contractDefaultValue = decl.DefaultValue;
                    }
                    else if (decl.DefaultText is not null)
                    {
                        try
                        {
                            contractDefaultValue = converter.ParseUntyped(decl.DefaultText);
                        }
                        catch (Exception ex)
                        {
                            throw new RuntimeCommandException(
                                def.Name,
                                $"Invalid contract default '{decl.DefaultText}' for parameter '{decl.Name}': {ex.Message}",
                                ex,
                                phase: CommandFailurePhase.Schema);
                        }
                    }

                    CommandContractValidation.ValidateValue(def.Name, decl, contractDefaultValue, CommandFailurePhase.Schema);
                    if (decl.DefaultValue is not null && descriptor is null)
                        throw new RuntimeCommandException(def.Name, $"Typed default for parameter '{decl.Name}' requires a formatter.", phase: CommandFailurePhase.Schema);

                    if (decl.Minimum.HasValue && contractDefaultValue is IConvertible convertible)
                    {
                        var num = convertible.ToDouble(CultureInfo.InvariantCulture);
                        if (num < decl.Minimum.Value)
                        {
                            throw new RuntimeCommandException(
                                def.Name,
                                $"Contract default '{decl.DefaultText ?? contractDefaultValue?.ToString()}' for parameter '{decl.Name}' must be at least {decl.Minimum.Value.ToString(CultureInfo.InvariantCulture)}.",
                                phase: CommandFailurePhase.Schema);
                        }
                    }
                }

                slots[i] = new BoundParameterSlot(
                    i,
                    decl.Name,
                    decl,
                    converter,
                    descriptor,
                    hasDefault,
                    contractDefaultValue,
                    decl.Minimum);

                slotIndicesByName.Add(decl.Name, i);
                memberSlots.Add(CommandContractValidation.MemberPath(decl), i);
            }

            var resolutionContext = new BindingResolutionContext(slotIndicesByName, slots, memberSlots);
            var boundCommand = command.CreateBoundCommand(resolutionContext, slots);

            builtEntries.Add(new RuntimeCommandCatalogEntry(
                boundCommand,
                registration.ProviderId,
                registration.Version));
        }

        var catalog = new RuntimeCommandCatalog(builtEntries);
        _ = RuntimeCommandManifest.FromCatalog(catalog, "runtime").ToJson();
        SealRegistrations();
        isSealed = true;
        return catalog;
    }

    private void EnsureMutable()
    {
        if (isRegistrationSealed || isSealed)
        {
            throw new RuntimeCommandException(
                "registry",
                "Commands must be registered before Runtime initialization.",
                phase: CommandFailurePhase.Schema);
        }
    }

    private sealed class EmptyServiceResolver : IServiceResolver
    {
        public static EmptyServiceResolver Instance { get; } = new();

        public TService Resolve<TService>() where TService : class =>
            throw new InvalidOperationException($"Service '{typeof(TService).FullName}' is not registered.");

        public bool TryGet<TService>(out TService? service) where TService : class
        {
            service = null;
            return false;
        }

        public object Resolve(Type serviceType) =>
            throw new InvalidOperationException($"Service '{serviceType.FullName}' is not registered.");
    }
}

public sealed class RuntimeCommandCatalog
{
    private readonly ReadOnlyDictionary<string, IBoundCommand> commandsByName;

    public RuntimeCommandCatalog(IReadOnlyList<IBoundCommand> commands)
        : this(commands.Select(command => new RuntimeCommandCatalogEntry(command, string.Empty, null)).ToArray())
    {
    }

    internal RuntimeCommandCatalog(IReadOnlyList<RuntimeCommandCatalogEntry> entries)
    {
        Entries = Array.AsReadOnly((entries ?? throw new ArgumentNullException(nameof(entries))).ToArray());
        Commands = new ReadOnlyCollection<IBoundCommand>(entries.Select(entry => entry.Command).ToArray());
        var dict = new Dictionary<string, IBoundCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var command = entry.Command;
            if (!dict.TryAdd(command.Name, command))
            {
                throw new RuntimeCommandException(
                    command.Name,
                    $"Duplicate command '{command.Name}' registered.",
                    recoverable: false,
                    phase: CommandFailurePhase.Schema);
            }
        }

        commandsByName = new ReadOnlyDictionary<string, IBoundCommand>(dict);
    }

    public IReadOnlyList<IBoundCommand> Commands { get; }

    public IReadOnlyList<RuntimeCommandCatalogEntry> Entries { get; }

    public IBoundCommand Get(string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        return commandsByName.TryGetValue(commandName, out var command)
            ? command
            : throw new RuntimeCommandException(
                commandName,
                "Command is not registered.",
                recoverable: true,
                phase: CommandFailurePhase.Execution);
    }

    public bool TryGet(string commandName, out IBoundCommand? command) =>
        commandsByName.TryGetValue(commandName, out command);

    public BoundCommand<TArguments> Get<TArguments>(string commandName)
        where TArguments : class
    {
        var cmd = Get(commandName);
        if (cmd is BoundCommand<TArguments> typed)
        {
            return typed;
        }

        throw new RuntimeCommandException(
            commandName,
            $"Command '{commandName}' arguments type is not '{typeof(TArguments).FullName}'.",
            phase: CommandFailurePhase.Binding);
    }

    public TCommand GetCommand<TCommand>(string commandName)
        where TCommand : class, IRuntimeCommand
    {
        var cmd = Get(commandName);
        if (cmd.Command is TCommand typed)
        {
            return typed;
        }

        if (cmd is TCommand typedBound)
        {
            return typedBound;
        }

        throw new RuntimeCommandException(
            commandName,
            $"Command '{commandName}' is not of type '{typeof(TCommand).FullName}'.",
            phase: CommandFailurePhase.Binding);
    }


}

public sealed class RuntimeCommandCatalogEntry
{
    public RuntimeCommandCatalogEntry(IBoundCommand command, string providerId, string? version)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        ProviderId = providerId ?? string.Empty;
        Version = version;
    }

    public IBoundCommand Command { get; }
    public string ProviderId { get; }
    public string? Version { get; }
}
