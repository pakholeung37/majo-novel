#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    void Register(IRuntimeCommandRegistrar commands);
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
    private bool isRegistrationSealed;
    private bool isSealed;

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

        var addedKeys = new List<string>();
        var recordingRegistrar = new RecordingRegistrar(this, addedKeys);
        try
        {
            module.Register(recordingRegistrar);
        }
        catch
        {
            foreach (var key in addedKeys)
            {
                registrations.Remove(key);
            }
            throw;
        }

        return this;
    }

    private sealed class RecordingRegistrar : IRuntimeCommandRegistrar
    {
        private readonly RuntimeCommandRegistry registry;
        private readonly List<string> addedKeys;

        public RecordingRegistrar(RuntimeCommandRegistry registry, List<string> addedKeys)
        {
            this.registry = registry;
            this.addedKeys = addedKeys;
        }

        public IRuntimeCommandRegistrar Add(
            string name,
            Func<IServiceResolver, IRuntimeCommand> factory,
            string providerId,
            string? version = null)
        {
            registry.Add(name, factory, providerId, version);
            var segments = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var normalizedName = string.Join(' ', segments);
            addedKeys.Add(normalizedName);
            return this;
        }
    }

    public void SealRegistrations()
    {
        isRegistrationSealed = true;
    }

    public RuntimeCommandCatalog BuildCatalog(
        IServiceResolver? services = null,
        CommandBindingEnvironment? environment = null)
    {
        isRegistrationSealed = true;
        isSealed = true;

        var resolver = services ?? EmptyServiceResolver.Instance;
        var env = environment ?? new CommandBindingEnvironment(new CommandValueConverterRegistry());
        env.Converters.Seal();

        var commands = new List<IRuntimeCommand>(registrations.Count);

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

            if (command is null || command.Schema is null)
            {
                throw new RuntimeCommandException(
                    registration.Name,
                    $"Command factory for '{registration.Name}' returned null command or schema.",
                    recoverable: false,
                    phase: CommandFailurePhase.Schema);
            }

            if (!string.Equals(command.Schema.Name, registration.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeCommandException(
                    registration.Name,
                    $"Command schema name '{command.Schema.Name}' does not match registered name '{registration.Name}'.",
                    recoverable: false,
                    phase: CommandFailurePhase.Schema);
            }

            command.Schema.Validate(env);
            command.Schema.AttachEnvironment(env);
            commands.Add(command);
        }

        return new RuntimeCommandCatalog(commands);
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
    private readonly ReadOnlyDictionary<string, IRuntimeCommand> commandsByName;

    public RuntimeCommandCatalog(IReadOnlyList<IRuntimeCommand> commands)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        var dict = new Dictionary<string, IRuntimeCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            if (!dict.TryAdd(command.Schema.Name, command))
            {
                throw new RuntimeCommandException(
                    command.Schema.Name,
                    $"Duplicate command '{command.Schema.Name}' registered.",
                    recoverable: false,
                    phase: CommandFailurePhase.Schema);
            }
        }

        commandsByName = new ReadOnlyDictionary<string, IRuntimeCommand>(dict);
    }

    public IReadOnlyList<IRuntimeCommand> Commands { get; }

    public IRuntimeCommand Get(string commandName)
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

    public bool TryGet(string commandName, out IRuntimeCommand? command) =>
        commandsByName.TryGetValue(commandName, out command);
}
