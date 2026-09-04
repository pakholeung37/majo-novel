#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RuntimeCommandDispatcher : IDisposable
{
    private readonly object operationGate = new();
    private readonly CommandBinder binder;
    private readonly IActorManager? actors;
    private readonly IOperationRunner? operations;
    private readonly IServiceResolver? services;
    private readonly IRuntimeLogger logger;
    private readonly bool abortOnRecoverableFailure;
    private readonly IRuntimeActivityRegistry? activities;
    private readonly Dictionary<string, RuntimeCommandActivity> activeCommands =
        new(StringComparer.Ordinal);
    private TaskCompletionSource commandQuiescence = CompletedQuiescence();
    private int activeCommandCount;

    public RuntimeCommandDispatcher(
        CommandBinder binder,
        IActorManager? actors = null,
        IOperationRunner? operations = null,
        IServiceResolver? services = null,
        IRuntimeLogger? logger = null,
        bool abortOnRecoverableFailure = false,
        IRuntimeActivityRegistry? activities = null)
    {
        this.binder = binder ?? throw new ArgumentNullException(nameof(binder));
        this.actors = actors;
        this.operations = operations;
        this.services = services;
        this.logger = logger ?? NullRuntimeLogger.Instance;
        this.abortOnRecoverableFailure = abortOnRecoverableFailure;
        if (activities is null && services is not null && services.TryGet<IRuntimeActivityRegistry>(out var resolved))
        {
            activities = resolved;
        }
        this.activities = activities;
    }

    public event Action<RuntimeCommandException>? CommandFailed;

    public RuntimeCommandException? LastFailure { get; private set; }

    public bool AbortOnRecoverableFailure => abortOnRecoverableFailure;

    public bool HasActiveCommands
    {
        get
        {
            lock (operationGate)
            {
                return activeCommandCount > 0;
            }
        }
    }

    public IReadOnlyList<RuntimeCommandActivity> ActiveCommands
    {
        get
        {
            lock (operationGate)
            {
                return activeCommands.Values.ToArray();
            }
        }
    }

    public void Validate(RuntimeCommandCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var command in catalog.Commands)
        {
            binder.Validate(command.Descriptor);
        }
    }

    public async Task ExecuteAsync(
        RuntimeCommandRegistration registration,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(arguments);
        IRuntimeLifetime? lifetime = null;
        IDialoguePlayer? dialogue = null;
        services?.TryGet(out lifetime);
        services?.TryGet(out dialogue);
        var host = lifetime as RuntimeHost;
        var executionId = Guid.NewGuid().ToString("N");
        using var activityLease = activities?.BeginActivity(
            RuntimeActivityOwner.Dialogue,
            "Command",
            registration.Name,
            isCancelable: true,
            isFinishable: false);
        BeginCommand(new RuntimeCommandActivity(
            registration.Name,
            executionId,
            dialogue?.CurrentLineId,
            DateTimeOffset.UtcNow));
        var correlation = new RuntimeLogCorrelation(
            host?.RuntimeId ?? string.Empty,
            host?.SessionId,
            (dialogue as DialoguePlayer)?.CurrentDialogueSessionId,
            executionId);
        using var correlationScope = logger.BeginCorrelationScope(correlation);
        var properties = new Dictionary<string, object?>
        {
            ["commandName"] = registration.Name,
            ["sourceLineId"] = dialogue?.CurrentLineId,
        };
        logger.Debug(
            "command",
            "command.started",
            $"Command '{registration.Name}' started with {arguments.Count} arguments.",
            correlation,
            properties);
        var context = new RuntimeCommandContext(
            actors,
            operations,
            services,
            cancellationToken,
            binder.Context);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registration.BindAndExecuteAsync(binder, arguments, context);
            logger.Debug(
                "command",
                "command.completed",
                $"Command '{registration.Name}' completed successfully.",
                correlation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Debug(
                "command",
                "command.cancelled",
                $"Command '{registration.Name}' cancelled.",
                correlation);
            // DialogueRunner owns dialogue cancellation; it is not a command failure.
        }
        catch (RuntimeCommandException exception)
        {
            LogCommandFailure(registration.Name, exception, correlation, properties);
            ReportFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RuntimeCommandException(
                registration.Name,
                exception.Message,
                exception,
                recoverable: false);
            var failureProperties = new Dictionary<string, object?>(properties)
            {
                ["recoverable"] = false,
                ["disposition"] = "abort",
            };
            logger.Error(
                "command",
                "command.faulted",
                $"Command '{registration.Name}' failed: {exception.Message}",
                exception,
                correlation,
                failureProperties);
            ReportFailure(failure);
            throw failure;
        }
        finally
        {
            EndCommand(executionId);
        }
    }

    public void Reset()
    {
        LastFailure = null;
        lock (operationGate)
        {
            activeCommands.Clear();
            activeCommandCount = 0;
            commandQuiescence.TrySetResult();
            commandQuiescence = CompletedQuiescence();
        }
    }

    public void ReportUnknownCommand(string commandText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        IRuntimeLifetime? lifetime = null;
        IDialoguePlayer? dialogue = null;
        services?.TryGet(out lifetime);
        services?.TryGet(out dialogue);
        var host = lifetime as RuntimeHost;
        var commandName = commandText.Split(
            (char[]?)null,
            2,
            StringSplitOptions.RemoveEmptyEntries)[0];
        var correlation = new RuntimeLogCorrelation(
            host?.RuntimeId ?? string.Empty,
            host?.SessionId,
            (dialogue as DialoguePlayer)?.CurrentDialogueSessionId,
            Guid.NewGuid().ToString("N"));
        var failure = new RuntimeCommandException(
            commandName,
            "Command is not registered.");
        var properties = new Dictionary<string, object?>
        {
            ["commandName"] = commandName,
            ["commandText"] = commandText,
            ["sourceLineId"] = dialogue?.CurrentLineId,
            ["disposition"] = abortOnRecoverableFailure ? "abort" : "skip",
        };
        if (abortOnRecoverableFailure)
        {
            logger.Error(
                "command",
                "command.unknown",
                $"Unknown Yarn command '{commandName}' aborted the dialogue.",
                failure,
                correlation,
                properties);
        }
        else
        {
            logger.Warning(
                "command",
                "command.unknown",
                $"Unknown Yarn command '{commandName}' was skipped.",
                correlation: correlation,
                properties: properties);
        }
        ReportFailure(failure);
    }

    private void LogCommandFailure(
        string commandName,
        RuntimeCommandException failure,
        RuntimeLogCorrelation correlation,
        IReadOnlyDictionary<string, object?> baseProperties)
    {
        var abort = !failure.IsRecoverable || abortOnRecoverableFailure;
        var properties = new Dictionary<string, object?>(baseProperties)
        {
            ["recoverable"] = failure.IsRecoverable,
            ["disposition"] = abort ? "abort" : "skip",
        };
        if (abort)
        {
            logger.Error(
                "command",
                "command.faulted",
                $"Command '{commandName}' failed: {failure.Message}",
                failure,
                correlation,
                properties);
        }
        else
        {
            logger.Warning(
                "command",
                "command.rejected",
                $"Command '{commandName}' was skipped: {failure.Message}",
                correlation: correlation,
                properties: properties);
        }
    }

    public async Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default)
    {
        Task commands;
        lock (operationGate)
        {
            commands = commandQuiescence.Task;
        }

        await commands.WaitAsync(cancellationToken);
    }

    public void Dispose() { }

    private void ReportFailure(RuntimeCommandException failure)
    {
        LastFailure = failure;
        CommandFailed?.Invoke(failure);
    }

    private void BeginCommand(RuntimeCommandActivity activity)
    {
        lock (operationGate)
        {
            activeCommands.Add(activity.ExecutionId, activity);
            if (activeCommandCount++ == 0)
            {
                commandQuiescence = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void EndCommand(string executionId)
    {
        lock (operationGate)
        {
            activeCommands.Remove(executionId);
            activeCommandCount--;
            if (activeCommandCount == 0)
            {
                commandQuiescence.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedQuiescence()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
