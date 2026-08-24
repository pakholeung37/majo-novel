#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue.Commands;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class YarnCommandAdapter : IYarnCommandAdapter
{
    private readonly IYarnCommandRegistry registry;
    private readonly RuntimeCommandCatalog catalog;
    private readonly RuntimeCommandDispatcher dispatcher;
    private readonly List<string> registeredCommands = new();
    private readonly List<CatalogCommandHandler> commandHandlers = new();
    private bool isInitialized;

    public YarnCommandAdapter(
        IYarnCommandRegistry registry,
        RuntimeCommandCatalog catalog,
        RuntimeCommandDispatcher dispatcher)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public event Action<RuntimeCommandException>? CommandFailed;

    public RuntimeCommandException? LastFailure => dispatcher.LastFailure;

    public bool HasActiveCommands => dispatcher.HasActiveCommands;

    public IReadOnlyList<RuntimeCommandActivity> ActiveCommands =>
        dispatcher.ActiveCommands;

    public void CancelDialogue() => registry.CancelDialogue();

    public Task DrainDialogueOperationsAsync(CancellationToken cancellationToken) =>
        dispatcher.DrainDialogueOperationsAsync(cancellationToken);

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        try
        {
            dispatcher.Validate(catalog);
            dispatcher.CommandFailed += HandleCommandFailed;
            foreach (var command in catalog.Commands)
            {
                var handler = new CatalogCommandHandler(
                    command,
                    dispatcher,
                    registry);
                commandHandlers.Add(handler);
                Register(command.Name, (Func<string[], Task>)handler.ExecuteAsync);
            }

            isInitialized = true;
            return Task.CompletedTask;
        }
        catch
        {
            dispatcher.CommandFailed -= HandleCommandFailed;
            UnregisterAll();
            throw;
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await dispatcher.DrainDialogueOperationsAsync(cancellationToken);
        dispatcher.Reset();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized && registeredCommands.Count == 0)
        {
            dispatcher.Dispose();
            registry.Dispose();
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await dispatcher.DrainDialogueOperationsAsync(cancellationToken);
            dispatcher.Reset();
            dispatcher.CommandFailed -= HandleCommandFailed;
            UnregisterAll();
            isInitialized = false;
        }
        finally
        {
            dispatcher.Dispose();
            registry.Dispose();
        }
    }

    private void HandleCommandFailed(RuntimeCommandException failure)
    {
        try
        {
            CommandFailed?.Invoke(failure);
        }
        finally
        {
            // A diagnostic listener must never prevent the dialogue from being aborted.
            registry.AbortDialogue();
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
        commandHandlers.Clear();
        if (exceptions is not null)
        {
            throw new AggregateException(
                "One or more Yarn command handlers failed to unregister.",
                exceptions);
        }
    }

    private sealed class CatalogCommandHandler
    {
        private readonly RuntimeCommandRegistration command;
        private readonly RuntimeCommandDispatcher dispatcher;
        private readonly IYarnCommandRegistry registry;

        public CatalogCommandHandler(
            RuntimeCommandRegistration command,
            RuntimeCommandDispatcher dispatcher,
            IYarnCommandRegistry registry)
        {
            this.command = command;
            this.dispatcher = dispatcher;
            this.registry = registry;
        }

        public Task ExecuteAsync(params string[] arguments) => dispatcher.ExecuteAsync(
            command,
            arguments,
            registry.DialogueCancellationToken);
    }
}
