#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class YarnCommandAdapter : IYarnCommandAdapter
{
    public static readonly IReadOnlyList<string> CommandNames =
        Array.AsReadOnly(new[] { "back", "char", "hide", "await", "endAwait", "cancel" });

    private readonly IYarnCommandRegistry registry;
    private readonly IRuntimeCommandService commands;
    private readonly RuntimeCommandParser parser;
    private readonly IActorManager? actors;
    private readonly IOperationScheduler? operations;
    private readonly IServiceResolver? services;
    private readonly RuntimeCommandRegistry extensions;
    private readonly List<string> registeredCommands = new();
    private readonly AwaitScopeCoordinator awaitScopes = new();
    private bool isInitialized;

    public YarnCommandAdapter(
        IYarnCommandRegistry registry,
        IRuntimeCommandService commands,
        RuntimeCommandParser parser)
        : this(registry, commands, parser, null, null, new RuntimeCommandRegistry(), null)
    {
    }

    public YarnCommandAdapter(
        IYarnCommandRegistry registry,
        IRuntimeCommandService commands,
        RuntimeCommandParser parser,
        IActorManager? actors,
        IOperationScheduler? operations,
        RuntimeCommandRegistry? extensions = null,
        IServiceResolver? services = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
        this.actors = actors;
        this.operations = operations;
        this.services = services;
        this.extensions = extensions ?? new RuntimeCommandRegistry();
    }

    public event Action<RuntimeCommandException>? CommandFailed;

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
            extensions.Seal();
            Register("back", (Func<string[], Task>)BackAsync);
            Register("char", (Func<string[], Task>)CharacterAsync);
            Register("hide", (Func<string[], Task>)HideAsync);
            Register("await", (Func<string[], Task>)AwaitAsync);
            Register("endAwait", (Func<string[], Task>)EndAwaitAsync);
            Register("cancel", (Func<string[], Task>)CancelAsync);
            foreach (var extension in extensions.Registrations)
            {
                Register(
                    extension.Name,
                    (Func<string[], Task>)(arguments => ExecuteAsync(
                        extension.Name,
                        context => extension.Handler(arguments, context))));
            }

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
        awaitScopes.Reset();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized && registeredCommands.Count == 0)
        {
            registry.Dispose();
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            awaitScopes.Reset();
            UnregisterAll();
            isInitialized = false;
            return Task.CompletedTask;
        }
        finally
        {
            awaitScopes.Dispose();
            registry.Dispose();
        }
    }

    private Task BackAsync(params string[] arguments) => ExecuteAsync(
        "back", context => commands.BackAsync(parser.ParseBack(arguments), context));

    private Task CharacterAsync(params string[] arguments) => ExecuteAsync(
        "char", context => commands.CharacterAsync(parser.ParseCharacter(arguments), context));

    private Task HideAsync(params string[] arguments) => ExecuteAsync(
        "hide", context => commands.HideAsync(parser.ParseHide(arguments), context));

    private Task AwaitAsync(params string[] arguments) => ExecuteAsync(
        "await",
        context => parser.ParseAwait(arguments) switch
        {
            BeginAwaitDirective begin => BeginAwaitAsync(begin, context),
            TargetAwaitDirective target => commands.AwaitAsync(target.Command, context),
            _ => throw new RuntimeCommandException("await", "Unknown await directive."),
        });

    private Task EndAwaitAsync(params string[] arguments) => ExecuteAsync(
        "endAwait",
        context =>
        {
            parser.ParseEndAwait(arguments);
            return awaitScopes.EndAsync(context.CancellationToken);
        });

    private Task CancelAsync(params string[] arguments) => ExecuteAsync(
        "cancel", context => commands.CancelAsync(parser.ParseCancel(arguments), context));

    private async Task ExecuteAsync(
        string commandName,
        Func<RuntimeCommandContext, Task> execute)
    {
        var context = new RuntimeCommandContext(
            actors,
            operations,
            services,
            registry.DialogueCancellationToken,
            awaitScopes.Track);
        try
        {
            await execute(context);
        }
        catch (OperationCanceledException) when (
            context.CancellationToken.IsCancellationRequested)
        {
            // DialogueRunner owns this cancellation; stopping dialogue is not a command failure.
        }
        catch (RuntimeCommandException exception)
        {
            ReportFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RuntimeCommandException(
                commandName,
                exception.Message,
                exception);
            ReportFailure(failure);
            throw failure;
        }
    }

    private Task BeginAwaitAsync(
        BeginAwaitDirective directive,
        RuntimeCommandContext context)
    {
        awaitScopes.Begin(directive.Complete, context.CancellationToken);
        return Task.CompletedTask;
    }

    private void ReportFailure(RuntimeCommandException failure)
    {
        LastFailure = failure;
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
        if (exceptions is not null)
        {
            throw new AggregateException(
                "One or more Yarn command handlers failed to unregister.",
                exceptions);
        }
    }
}
