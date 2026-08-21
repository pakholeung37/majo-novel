#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class YarnCommandAdapter : IYarnCommandAdapter
{
    public static readonly IReadOnlyList<string> CommandNames =
        Array.AsReadOnly(new[] { "back", "char", "hide", "await", "cancel" });

    private readonly IYarnCommandRegistry registry;
    private readonly IRuntimeCommandService commands;
    private readonly RuntimeCommandParser parser;
    private readonly List<string> registeredCommands = new();
    private bool isInitialized;

    public YarnCommandAdapter(
        IYarnCommandRegistry registry,
        IRuntimeCommandService commands,
        RuntimeCommandParser parser)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    public RuntimeCommandException? LastFailure { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        try
        {
            Register("back", (Func<string[], Task>)BackAsync);
            Register("char", (Func<string[], Task>)CharacterAsync);
            Register("hide", (Func<string[], Task>)HideAsync);
            Register("await", (Func<string[], Task>)AwaitAsync);
            Register("cancel", (Func<string[], Task>)CancelAsync);
            isInitialized = true;
            return Task.CompletedTask;
        }
        catch
        {
            UnregisterAll();
            throw;
        }
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastFailure = null;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized && registeredCommands.Count == 0)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        UnregisterAll();
        isInitialized = false;
        return Task.CompletedTask;
    }

    private Task BackAsync(params string[] arguments) => ExecuteAsync(
        "back", () => commands.BackAsync(parser.ParseBack(arguments)));

    private Task CharacterAsync(params string[] arguments) => ExecuteAsync(
        "char", () => commands.CharacterAsync(parser.ParseCharacter(arguments)));

    private Task HideAsync(params string[] arguments) => ExecuteAsync(
        "hide", () => commands.HideAsync(parser.ParseHide(arguments)));

    private Task AwaitAsync(params string[] arguments) => ExecuteAsync(
        "await", () => commands.AwaitAsync(parser.ParseAwait(arguments)));

    private Task CancelAsync(params string[] arguments) => ExecuteAsync(
        "cancel", () => commands.CancelAsync(parser.ParseCancel(arguments)));

    private async Task ExecuteAsync(string commandName, Func<Task> execute)
    {
        try
        {
            await execute();
        }
        catch (RuntimeCommandException exception)
        {
            LastFailure = exception;
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RuntimeCommandException(
                commandName,
                exception.Message,
                exception);
            LastFailure = failure;
            throw failure;
        }
    }

    private void Register(string commandName, Delegate handler)
    {
        registry.AddCommandHandler(commandName, handler);
        registeredCommands.Add(commandName);
    }

    private void UnregisterAll()
    {
        List<Exception>? exceptions = null;
        for (var index = registeredCommands.Count - 1; index >= 0; index--)
        {
            try
            {
                registry.RemoveCommandHandler(registeredCommands[index]);
            }
            catch (Exception exception)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(exception);
            }
        }

        registeredCommands.Clear();
        if (exceptions is not null)
        {
            throw new AggregateException(
                "One or more Yarn command handlers failed to unregister.",
                exceptions);
        }
    }
}
