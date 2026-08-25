#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Operations;

public sealed class OperationScheduler : IOperationScheduler
{
    private readonly object gate = new();
    private readonly IFrameClock frameClock;
    private readonly IRuntimeLifetime lifetime;
    private readonly IRuntimeLogger logger;
    private readonly HashSet<OperationHandle> activeOperations = new();
    private readonly Dictionary<string, OperationHandle> operationsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<OperationHandle>> operationsByGroup =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationHandle> completedOperationsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<OperationHandle>> completedOperationsByGroup =
        new(StringComparer.Ordinal);
    private bool isInitialized;
    private bool acceptsOperations;

    public OperationScheduler(
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
            completedOperationsById.Clear();
            completedOperationsByGroup.Clear();
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
            completedOperationsById.Clear();
            completedOperationsByGroup.Clear();
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
                throw new OperationSchedulerException(
                    "OperationScheduler is resetting or shutting down.");
            }

            if (definition.Id is not null && operationsById.ContainsKey(definition.Id))
            {
                throw new DuplicateOperationIdException(definition.Id);
            }

            if (definition.Id is not null)
            {
                completedOperationsById.Remove(definition.Id);
            }

            handle = new OperationHandle(
                definition,
                frameClock,
                lifetime.SessionCancellationToken,
                RemoveFromActiveOperations);
            activeOperations.Add(handle);

            if (definition.Id is not null)
            {
                operationsById.Add(definition.Id, handle);
            }

            if (definition.GroupId is not null)
            {
                if (!operationsByGroup.TryGetValue(definition.GroupId, out var group))
                {
                    group = new HashSet<OperationHandle>();
                    operationsByGroup.Add(definition.GroupId, group);
                }

                group.Add(handle);
            }
        }

        handle.Start();
        logger.Debug(
            "operation",
            "operation.started",
            $"Operation '{handle.Id ?? "<anonymous>"}' (Group: '{handle.GroupId ?? "<none>"}') started.",
            new RuntimeLogCorrelation(string.Empty, operationId: handle.DiagnosticId));
        _ = ObserveCompletionAsync(handle);
        return handle;
    }

    public Task WaitAsync(
        OperationTarget target,
        CancellationToken cancellationToken = default)
    {
        var handles = ResolveTarget(target);
        logger.Debug(
            "operation",
            "operation.wait_started",
            $"Waiting for operation target '{target.Kind}:{target.Name}'.");
        return WaitForHandlesAsync(handles, cancellationToken);
    }

    public Task CancelAsync(
        OperationTarget target,
        CancellationToken cancellationToken = default)
    {
        var handles = ResolveTarget(target);
        logger.Debug(
            "operation",
            "operation.cancel_requested",
            $"Cancelling operation target '{target.Kind}:{target.Name}'.");
        return CancelHandlesAsync(handles, cancellationToken);
    }

    public Task CompleteAsync(
        OperationTarget target,
        CancellationToken cancellationToken = default)
    {
        var handles = ResolveTarget(target);
        logger.Debug(
            "operation",
            "operation.complete_requested",
            $"Completing operation target '{target.Kind}:{target.Name}'.");
        foreach (var handle in handles)
        {
            handle.Complete();
        }

        return WaitForHandlesAsync(handles, cancellationToken);
    }

    public Task CompleteSkippableAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        OperationHandle[] handles;
        lock (gate)
        {
            EnsureInitialized();
            handles = operationsByGroup.TryGetValue(groupId, out var group)
                ? group.Where(value => value.IsSkippable).ToArray()
                : Array.Empty<OperationHandle>();
        }
        foreach (var handle in handles) handle.Complete();
        return WaitForHandlesAsync(handles, cancellationToken);
    }

    private OperationHandle[] ResolveTarget(OperationTarget target)
    {
        lock (gate)
        {
            EnsureInitialized();
            if (target.Kind == OperationTargetKind.Id &&
                operationsById.TryGetValue(target.Name, out var operation))
            {
                return [operation];
            }

            if (target.Kind == OperationTargetKind.Id &&
                completedOperationsById.TryGetValue(target.Name, out var completedOperation))
            {
                return [completedOperation];
            }

            if (target.Kind == OperationTargetKind.Group)
            {
                var handles = new HashSet<OperationHandle>();
                if (operationsByGroup.TryGetValue(target.Name, out var group))
                {
                    handles.UnionWith(group);
                }

                if (completedOperationsByGroup.TryGetValue(
                        target.Name, out var completedGroup))
                {
                    handles.UnionWith(completedGroup);
                }

                if (handles.Count > 0)
                {
                    return handles.ToArray();
                }
            }
        }

        throw new OperationNotFoundException(target);
    }

    private static async Task WaitForHandlesAsync(
        IReadOnlyCollection<OperationHandle> handles,
        CancellationToken cancellationToken)
    {
        var completions = handles.Select(handle => handle.Completion).ToArray();
        await Task.WhenAll(completions).WaitAsync(cancellationToken);
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
                $"Operation '{handle.Id ?? "<anonymous>"}' completed with state '{handle.State}'.",
                new RuntimeLogCorrelation(string.Empty, operationId: handle.DiagnosticId));
        }
        catch (OperationCanceledException)
        {
            logger.Debug(
                "operation",
                "operation.cancelled",
                $"Operation '{handle.Id ?? "<anonymous>"}' cancelled.",
                new RuntimeLogCorrelation(string.Empty, operationId: handle.DiagnosticId));
        }
        catch (Exception exception)
        {
            logger.Error(
                "operation",
                "operation.faulted",
                $"Operation '{handle.Id ?? "<anonymous>"}' faulted.",
                exception,
                new RuntimeLogCorrelation(string.Empty, operationId: handle.DiagnosticId));
        }
    }

    private void RemoveFromActiveOperations(OperationHandle handle)
    {
        lock (gate)
        {
            activeOperations.Remove(handle);

            if (handle.Id is not null)
            {
                operationsById.Remove(handle.Id);
                completedOperationsById[handle.Id] = handle;
            }

            if (handle.GroupId is not null &&
                operationsByGroup.TryGetValue(handle.GroupId, out var group))
            {
                group.Remove(handle);
                if (group.Count == 0)
                {
                    operationsByGroup.Remove(handle.GroupId);
                }

                if (!completedOperationsByGroup.TryGetValue(
                        handle.GroupId, out var completedGroup))
                {
                    completedGroup = new HashSet<OperationHandle>();
                    completedOperationsByGroup.Add(handle.GroupId, completedGroup);
                }

                completedGroup.Add(handle);
            }
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new OperationSchedulerException("OperationScheduler is not initialized.");
        }
    }
}
