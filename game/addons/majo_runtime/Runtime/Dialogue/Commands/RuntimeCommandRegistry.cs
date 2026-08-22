#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

public interface IRuntimeCommandHandler<in TArguments>
    where TArguments : class, new()
{
    Task ExecuteAsync(TArguments arguments, RuntimeCommandContext context);
}

public interface IRuntimeCommandRegistrar
{
    IRuntimeCommandRegistrar Register<TArguments>(IRuntimeCommandHandler<TArguments> handler)
        where TArguments : class, new();

    IRuntimeCommandRegistrar Register<TArguments>(
        Func<TArguments, RuntimeCommandContext, Task> handler)
        where TArguments : class, new();
}

public interface IRuntimeCommandModule
{
    void Register(IRuntimeCommandRegistrar commands);
}

public sealed class RuntimeCommandRegistry : IRuntimeCommandRegistrar
{
    private static readonly HashSet<string> YarnReservedNames = new(
        new[] { "wait", "stop" },
        StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, RuntimeCommandRegistration> registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private bool isSealed;

    public IReadOnlyCollection<RuntimeCommandRegistration> Registrations =>
        new ReadOnlyCollection<RuntimeCommandRegistration>(
            new List<RuntimeCommandRegistration>(registrations.Values));

    public RuntimeCommandRegistry Register<TArguments>(
        IRuntimeCommandHandler<TArguments> handler)
        where TArguments : class, new()
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register<TArguments>(handler.ExecuteAsync);
    }

    public RuntimeCommandRegistry Register<TArguments>(
        Func<TArguments, RuntimeCommandContext, Task> handler)
        where TArguments : class, new()
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureMutable();
        var descriptor = CommandDescriptor.Create<TArguments>();
        if (YarnReservedNames.Contains(descriptor.Name))
        {
            throw new RuntimeCommandException(
                descriptor.Name,
                "Command name is reserved by Yarn Spinner.");
        }

        var registration = new RuntimeCommandRegistration<TArguments>(descriptor, handler);
        if (!registrations.TryAdd(descriptor.Name, registration))
        {
            throw new RuntimeCommandException(
                descriptor.Name,
                "Command is already registered.");
        }

        return this;
    }

    IRuntimeCommandRegistrar IRuntimeCommandRegistrar.Register<TArguments>(
        IRuntimeCommandHandler<TArguments> handler) => Register(handler);

    IRuntimeCommandRegistrar IRuntimeCommandRegistrar.Register<TArguments>(
        Func<TArguments, RuntimeCommandContext, Task> handler) => Register(handler);

    public RuntimeCommandRegistry RegisterModule(IRuntimeCommandModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        EnsureMutable();
        var existingNames = new HashSet<string>(
            registrations.Keys,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            module.Register(this);
            return this;
        }
        catch
        {
            foreach (var commandName in registrations.Keys
                         .Where(name => !existingNames.Contains(name))
                         .ToArray())
            {
                registrations.Remove(commandName);
            }

            throw;
        }
    }

    public RuntimeCommandCatalog BuildCatalog()
    {
        isSealed = true;
        return new RuntimeCommandCatalog(
            new ReadOnlyCollection<RuntimeCommandRegistration>(
                new List<RuntimeCommandRegistration>(registrations.Values)));
    }

    private void EnsureMutable()
    {
        if (isSealed)
        {
            throw new RuntimeCommandException(
                "registry",
                "Commands must be registered before Runtime initialization.");
        }
    }
}

public abstract class RuntimeCommandRegistration
{
    protected RuntimeCommandRegistration(CommandDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public string Name => Descriptor.Name;

    public CommandDescriptor Descriptor { get; }

    internal abstract Task BindAndExecuteAsync(
        CommandBinder binder,
        IReadOnlyList<string> arguments,
        RuntimeCommandContext context);
}

internal sealed class RuntimeCommandRegistration<TArguments> : RuntimeCommandRegistration
    where TArguments : class, new()
{
    private readonly Func<TArguments, RuntimeCommandContext, Task> handler;

    public RuntimeCommandRegistration(
        CommandDescriptor descriptor,
        Func<TArguments, RuntimeCommandContext, Task> handler)
        : base(descriptor)
    {
        this.handler = handler;
    }

    internal override Task BindAndExecuteAsync(
        CommandBinder binder,
        IReadOnlyList<string> arguments,
        RuntimeCommandContext context) =>
        handler(binder.Bind<TArguments>(Descriptor, arguments), context);
}

public sealed class RuntimeCommandCatalog
{
    private readonly IReadOnlyDictionary<string, RuntimeCommandRegistration> commandsByName;

    internal RuntimeCommandCatalog(IReadOnlyList<RuntimeCommandRegistration> commands)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        var indexed = new Dictionary<string, RuntimeCommandRegistration>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            indexed.Add(command.Name, command);
        }

        commandsByName = new ReadOnlyDictionary<string, RuntimeCommandRegistration>(indexed);
    }

    public IReadOnlyList<RuntimeCommandRegistration> Commands { get; }

    public RuntimeCommandRegistration Get(string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        return commandsByName.TryGetValue(commandName, out var command)
            ? command
            : throw new RuntimeCommandException(commandName, "Command is not registered.");
    }
}
