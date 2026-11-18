#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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

    public RuntimeCommandCatalog Catalog => catalog;

    public Func<string, IReadOnlyList<string>, CancellationToken, Task>? PrepareCommandAsync { get; set; }

    public event Action<RuntimeCommandException>? CommandFailed;

    public RuntimeCommandException? LastFailure => dispatcher.LastFailure;

    public bool AbortOnRecoverableFailure => dispatcher.AbortOnRecoverableFailure;

    public bool HasActiveCommands => dispatcher.HasActiveCommands;

    public IReadOnlyList<RuntimeCommandActivity> ActiveCommands =>
        dispatcher.ActiveCommands;

    public void CancelDialogue() => registry.CancelDialogue();

    public Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default) =>
        dispatcher.WaitForQuiescenceAsync(cancellationToken);

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        try
        {
            dispatcher.CommandFailed += HandleCommandFailed;
            registry.UnhandledCommand += HandleUnhandledCommand;
            foreach (var group in catalog.Commands.GroupBy(
                         command => command.RootName,
                         StringComparer.OrdinalIgnoreCase))
            {
                var handler = new CatalogCommandHandler(
                    group.Key,
                    catalog,
                    dispatcher,
                    registry,
                    dispatcher.AbortOnRecoverableFailure,
                    (root, args, token) => PrepareCommandAsync?.Invoke(root, args, token) ?? Task.CompletedTask);
                commandHandlers.Add(handler);
                Register(group.Key, (Func<string[], Task>)handler.ExecuteAsync);
            }

            isInitialized = true;
            return Task.CompletedTask;
        }
        catch
        {
            dispatcher.CommandFailed -= HandleCommandFailed;
            registry.UnhandledCommand -= HandleUnhandledCommand;
            UnregisterAll();
            throw;
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await dispatcher.WaitForQuiescenceAsync(cancellationToken);
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
            await dispatcher.WaitForQuiescenceAsync(cancellationToken);
            dispatcher.Reset();
            dispatcher.CommandFailed -= HandleCommandFailed;
            registry.UnhandledCommand -= HandleUnhandledCommand;
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
            // Unexpected runtime faults remain fatal to the current dialogue. Recoverable
            // content errors are skipped unless strict authoring behaviour was requested.
            if (dispatcher.AbortOnRecoverableFailure || !failure.IsRecoverable)
            {
                registry.AbortDialogue();
            }
        }
    }

    private void HandleUnhandledCommand(string commandText) =>
        dispatcher.ReportUnknownCommand(commandText);

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
        private readonly string rootName;
        private readonly RuntimeCommandCatalog catalog;
        private readonly RuntimeCommandDispatcher dispatcher;
        private readonly IYarnCommandRegistry registry;
        private readonly bool abortOnRecoverableFailure;
        private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task> prepare;

        public CatalogCommandHandler(
            string rootName,
            RuntimeCommandCatalog catalog,
            RuntimeCommandDispatcher dispatcher,
            IYarnCommandRegistry registry,
            bool abortOnRecoverableFailure,
            Func<string, IReadOnlyList<string>, CancellationToken, Task> prepare)
        {
            this.rootName = rootName;
            this.catalog = catalog;
            this.dispatcher = dispatcher;
            this.registry = registry;
            this.abortOnRecoverableFailure = abortOnRecoverableFailure;
            this.prepare = prepare;
        }

        public async Task ExecuteAsync(params string[] arguments)
        {
            try
            {
                var tokens = new[] { rootName }.Concat(arguments).ToArray();
                var command = catalog.Resolve(tokens, out var input);
                if (command is null)
                {
                    dispatcher.ReportUnknownCommand(string.Join(
                        ' ', new[] { rootName }.Concat(arguments)));
                    return;
                }

                await dispatcher.ExecuteAsync(
                    command,
                    input!,
                    registry.DialogueCancellationToken,
                    () => prepare(rootName, arguments, registry.DialogueCancellationToken));
            }
            catch (RuntimeCommandException exception) when (
                exception.IsRecoverable && !abortOnRecoverableFailure)
            {
                // The dispatcher already recorded and published the failure. Returning a
                // completed task lets Yarn advance to the next instruction.
            }
        }

    }
}
