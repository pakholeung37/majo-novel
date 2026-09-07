#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RuntimeCommandDispatcher : IDisposable
{
    private readonly object operationGate = new();
    private readonly IServiceResolver? services;
    private readonly IRuntimeLogger logger;
    private readonly bool abortOnRecoverableFailure;
    private readonly IRuntimeActivityRegistry? activities;
    private readonly Dictionary<string, RuntimeCommandActivity> activeCommands =
        new(StringComparer.Ordinal);
    private TaskCompletionSource commandQuiescence = CompletedQuiescence();
    private int activeCommandCount;

    public RuntimeCommandDispatcher(
        IServiceResolver? services = null,
        IRuntimeLogger? logger = null,
        bool abortOnRecoverableFailure = false,
        IRuntimeActivityRegistry? activities = null)
    {
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

    public async Task ExecuteAsync(
        IRuntimeCommand command,
        CommandInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(input);

        IRuntimeLifetime? lifetime = null;
        IDialoguePlayer? dialogue = null;
        services?.TryGet(out lifetime);
        services?.TryGet(out dialogue);
        var host = lifetime as RuntimeHost;
        var executionId = Guid.NewGuid().ToString("N");

        using var activityLease = activities?.BeginActivity(
            RuntimeActivityOwner.Dialogue,
            "Command",
            command.Name,
            isCancelable: true,
            isFinishable: false);

        BeginCommand(new RuntimeCommandActivity(
            command.Name,
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
            ["commandName"] = command.Name,
            ["sourceLineId"] = dialogue?.CurrentLineId,
        };

        logger.Debug(
            "command",
            "command.started",
            $"Command '{command.Name}' started with {input.RawArguments.Count} arguments.",
            correlation,
            properties);

        var context = new CommandExecutionContext(
            executionId,
            dialogue?.CurrentLineId,
            (dialogue as DialoguePlayer)?.CurrentDialogueSessionId,
            cancellationToken: cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await command.ExecuteAsync(input, context);
            logger.Debug(
                "command",
                "command.completed",
                $"Command '{command.Name}' completed successfully.",
                correlation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Debug(
                "command",
                "command.cancelled",
                $"Command '{command.Name}' cancelled.",
                correlation);
            // DialogueRunner owns dialogue cancellation; it is not a command failure.
        }
        catch (RuntimeCommandException exception)
        {
            LogCommandFailure(command.Name, exception, correlation, properties);
            ReportFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RuntimeCommandException(
                command.Name,
                exception.Message,
                exception,
                recoverable: false,
                phase: CommandFailurePhase.Execution);
            var failureProperties = new Dictionary<string, object?>(properties)
            {
                ["recoverable"] = false,
                ["disposition"] = "abort",
                ["failurePhase"] = "execution",
            };
            logger.Error(
                "command",
                "command.faulted",
                $"Command '{command.Name}' failed: {exception.Message}",
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
            "Command is not registered.",
            phase: CommandFailurePhase.Execution);
        var properties = new Dictionary<string, object?>
        {
            ["commandName"] = commandName,
            ["commandText"] = commandText,
            ["sourceLineId"] = dialogue?.CurrentLineId,
            ["disposition"] = abortOnRecoverableFailure ? "abort" : "skip",
            ["failurePhase"] = "execution",
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
            ["failurePhase"] = failure.FailurePhase.ToString().ToLowerInvariant(),
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
