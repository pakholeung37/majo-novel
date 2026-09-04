#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Operations;

public sealed class OperationRunner : IOperationRunner
{
    private readonly object gate = new();
    private readonly IFrameClock frameClock;
    private readonly IRuntimeLifetime lifetime;
    private readonly IRuntimeLogger logger;
    private readonly HashSet<OperationHandle> activeOperations = new();
    private bool isInitialized;
    private bool acceptsOperations;

    public OperationRunner(
        IFrameClock frameClock,
        IRuntimeLifetime lifetime,
        IRuntimeLogger? logger = null)
    {
        this.frameClock = frameClock ?? throw new ArgumentNullException(nameof(frameClock));
        this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public IReadOnlyCollection<IOperationHandle> ActiveOperations
    {
        get
        {
            lock (gate)
            {
                return activeOperations.Cast<IOperationHandle>().ToArray();
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (isInitialized)
            {
                return Task.CompletedTask;
            }

            isInitialized = true;
            acceptsOperations = true;
        }

        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        OperationHandle[] snapshot;
        lock (gate)
        {
            EnsureInitialized();
            acceptsOperations = false;
            snapshot = activeOperations.ToArray();
        }

        await CancelHandlesAsync(snapshot, cancellationToken);

        lock (gate)
        {
            if (isInitialized)
            {
                acceptsOperations = true;
            }
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        OperationHandle[] snapshot;
        lock (gate)
        {
            if (!isInitialized)
            {
                return;
            }

            acceptsOperations = false;
            snapshot = activeOperations.ToArray();
        }

        await CancelHandlesAsync(snapshot, cancellationToken);
        lock (gate)
        {
            isInitialized = false;
        }
    }

    public IOperationHandle Start(OperationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        OperationHandle handle;
        lock (gate)
        {
            EnsureInitialized();
            if (!acceptsOperations)
            {
                throw new OperationRunnerException(
                    "OperationRunner is resetting or shutting down.");
            }

            handle = new OperationHandle(
                definition,
                frameClock,
                RemoveFromActiveOperations,
                lifetime.SessionCancellationToken);
            activeOperations.Add(handle);
        }

        handle.Start();
        logger.Debug(
            "operation",
            "operation.started",
            "Operation started.",
            new RuntimeLogCorrelation(string.Empty, operationId: handle.DiagnosticId));
        _ = ObserveCompletionAsync(handle);
        return handle;
    }

    private static async Task CancelHandlesAsync(
        IReadOnlyCollection<OperationHandle> handles,
        CancellationToken cancellationToken)
    {
        foreach (var handle in handles)
        {
            handle.Cancel();
        }

        var exceptions = new List<Exception>();
        foreach (var handle in handles)
        {
            try
            {
                await handle.Completion.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (
                handle.State == OperationState.Cancelled)
            {
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }

        if (exceptions.Count > 0)
        {
            throw new AggregateException("One or more operations failed during cancellation.",
                exceptions);
        }
    }

    private async Task ObserveCompletionAsync(OperationHandle handle)
    {
        try
        {
            await handle.Completion;
            logger.Debug(
                "operation",
                "operation.completed",
                $"Operation completed with state '{handle.State}'.",
                new RuntimeLogCorrelation(string.Empty, operationId: handle.DiagnosticId));
        }
        catch (OperationCanceledException)
        {
            logger.Debug(
                "operation",
                "operation.cancelled",
                "Operation cancelled.",
                new RuntimeLogCorrelation(string.Empty, operationId: handle.DiagnosticId));
        }
        catch (Exception exception)
        {
            logger.Error(
                "operation",
                "operation.faulted",
                "Operation faulted.",
                exception,
                new RuntimeLogCorrelation(string.Empty, operationId: handle.DiagnosticId));
        }
    }

    private void RemoveFromActiveOperations(OperationHandle handle)
    {
        lock (gate)
        {
            activeOperations.Remove(handle);
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new OperationRunnerException("OperationRunner is not initialized.");
        }
    }
}
