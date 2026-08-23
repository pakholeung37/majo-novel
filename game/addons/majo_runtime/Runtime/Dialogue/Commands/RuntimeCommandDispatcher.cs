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
    private readonly IOperationScheduler? operations;
    private readonly IServiceResolver? services;
    private readonly AwaitScopeCoordinator awaitScopes = new();
    private readonly HashSet<IOperationHandle> dialogueOperations =
        new(ReferenceEqualityComparer.Instance);
    private TaskCompletionSource commandQuiescence = CompletedQuiescence();
    private int activeCommandCount;

    public RuntimeCommandDispatcher(
        CommandBinder binder,
        IActorManager? actors = null,
        IOperationScheduler? operations = null,
        IServiceResolver? services = null)
    {
        this.binder = binder ?? throw new ArgumentNullException(nameof(binder));
        this.actors = actors;
        this.operations = operations;
        this.services = services;
    }

    public event Action<RuntimeCommandException>? CommandFailed;

    public RuntimeCommandException? LastFailure { get; private set; }

    public bool HasActiveCommands
    {
        get
        {
            lock (operationGate)
            {
                return activeCommandCount > 0 || dialogueOperations.Count > 0;
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
        BeginCommand();
        var context = new RuntimeCommandContext(
            actors,
            operations,
            services,
            cancellationToken,
            TrackOperation,
            awaitScopes);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registration.BindAndExecuteAsync(binder, arguments, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // DialogueRunner owns dialogue cancellation; it is not a command failure.
        }
        catch (RuntimeCommandException exception)
        {
            ReportFailure(exception);
            throw;
        }
        catch (Exception exception)
        {
            var failure = new RuntimeCommandException(
                registration.Name,
                exception.Message,
                exception);
            ReportFailure(failure);
            throw failure;
        }
        finally
        {
            EndCommand();
        }
    }

    public void Reset()
    {
        LastFailure = null;
        awaitScopes.Reset();
        lock (operationGate)
        {
            dialogueOperations.Clear();
        }
    }

    public async Task DrainDialogueOperationsAsync(CancellationToken cancellationToken)
    {
        IOperationHandle[] operationsToDrain;
        lock (operationGate)
        {
            operationsToDrain = dialogueOperations.ToArray();
        }

        foreach (var operation in operationsToDrain)
        {
            operation.Cancel();
        }

        await Task.WhenAll(operationsToDrain.Select(
            operation => ObserveCancellationAsync(operation, cancellationToken)));

        Task commands;
        lock (operationGate)
        {
            commands = commandQuiescence.Task;
        }

        await commands.WaitAsync(cancellationToken);
    }

    public void Dispose() => awaitScopes.Dispose();

    private void ReportFailure(RuntimeCommandException failure)
    {
        LastFailure = failure;
        CommandFailed?.Invoke(failure);
    }

    private void TrackOperation(IOperationHandle operation)
    {
        awaitScopes.Track(operation);
        lock (operationGate)
        {
            dialogueOperations.Add(operation);
        }

        _ = RemoveCompletedOperationAsync(operation);
    }

    private void BeginCommand()
    {
        lock (operationGate)
        {
            if (activeCommandCount++ == 0)
            {
                commandQuiescence = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void EndCommand()
    {
        lock (operationGate)
        {
            activeCommandCount--;
            if (activeCommandCount == 0)
            {
                commandQuiescence.TrySetResult();
            }
        }
    }

    private async Task RemoveCompletedOperationAsync(IOperationHandle operation)
    {
        try
        {
            await operation.Completion;
        }
        catch
        {
            // OperationScheduler owns terminal fault observation.
        }
        finally
        {
            lock (operationGate)
            {
                dialogueOperations.Remove(operation);
            }
        }
    }

    private static async Task ObserveCancellationAsync(
        IOperationHandle operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (
            operation.State == OperationState.Cancelled)
        {
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
