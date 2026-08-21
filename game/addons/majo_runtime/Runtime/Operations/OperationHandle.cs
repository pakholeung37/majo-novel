#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Operations;

internal sealed class OperationHandle : IOperationHandle
{
    private readonly object gate = new();
    private readonly OperationDefinition definition;
    private readonly OperationContext context;
    private readonly Action<OperationHandle> onTerminated;
    private readonly CancellationTokenSource executionCancellation = new();
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration lifetimeCancellationRegistration;
    private OperationControlRequest controlRequest;
    private OperationState state = OperationState.Created;
    private Exception? cancellationCallbackException;
    private bool started;

    public OperationHandle(
        OperationDefinition definition,
        IFrameClock frameClock,
        CancellationToken lifetimeCancellationToken,
        Action<OperationHandle> onTerminated)
    {
        this.definition = definition;
        this.onTerminated = onTerminated;
        context = new OperationContext(
            frameClock,
            executionCancellation.Token,
            definition.Id,
            definition.GroupId);
        lifetimeCancellationRegistration = lifetimeCancellationToken.Register(
            static state => ((OperationHandle)state!).Cancel(), this);
    }

    public string? Id => definition.Id;

    public string? GroupId => definition.GroupId;

    public OperationState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public Task Completion => completion.Task;

    public void Start()
    {
        lock (gate)
        {
            if (started)
            {
                throw new InvalidOperationException("Operation has already started.");
            }

            started = true;
            if (state == OperationState.Created)
            {
                state = OperationState.Running;
            }
        }

        _ = RunAsync();
    }

    public void Cancel() => RequestControl(
        OperationControlRequest.Cancel,
        OperationState.Cancelling);

    public void Complete() => RequestControl(
        OperationControlRequest.Complete,
        OperationState.Completing);

    private void RequestControl(
        OperationControlRequest request,
        OperationState requestedState)
    {
        lock (gate)
        {
            if (IsTerminal(state) || controlRequest != OperationControlRequest.None)
            {
                return;
            }

            controlRequest = request;
            state = requestedState;
        }

        try
        {
            executionCancellation.Cancel();
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                cancellationCallbackException = exception;
            }
        }
    }

    private async Task RunAsync()
    {
        Exception? executionException = null;
        try
        {
            executionCancellation.Token.ThrowIfCancellationRequested();
            await definition.ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            executionException = exception;
        }

        OperationControlRequest request;
        Exception? cancellationException;
        lock (gate)
        {
            request = controlRequest;
            cancellationException = cancellationCallbackException;
        }

        Exception? finalizationException = null;
        try
        {
            if (request == OperationControlRequest.Complete)
            {
                await definition.CompleteAsync(CancellationToken.None);
            }
            else if (request == OperationControlRequest.Cancel)
            {
                await definition.CancelAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            finalizationException = exception;
        }

        var exceptions = new List<Exception>(3);
        if (executionException is not null)
        {
            exceptions.Add(executionException);
        }

        if (cancellationException is not null)
        {
            exceptions.Add(cancellationException);
        }

        if (finalizationException is not null)
        {
            exceptions.Add(finalizationException);
        }

        if (exceptions.Count > 0)
        {
            FinishFaulted(exceptions);
        }
        else if (request == OperationControlRequest.Cancel)
        {
            FinishCancelled();
        }
        else
        {
            FinishCompleted();
        }

        lifetimeCancellationRegistration.Dispose();
        executionCancellation.Dispose();
    }

    private void FinishCompleted()
    {
        lock (gate)
        {
            state = OperationState.Completed;
        }

        onTerminated(this);
        completion.TrySetResult();
    }

    private void FinishCancelled()
    {
        lock (gate)
        {
            state = OperationState.Cancelled;
        }

        onTerminated(this);
        completion.TrySetCanceled();
    }

    private void FinishFaulted(IReadOnlyCollection<Exception> exceptions)
    {
        lock (gate)
        {
            state = OperationState.Faulted;
        }

        onTerminated(this);
        completion.TrySetException(exceptions.Count == 1
            ? exceptions.First()
            : new AggregateException(exceptions));
    }

    private static bool IsTerminal(OperationState value) =>
        value is OperationState.Completed or OperationState.Cancelled or OperationState.Faulted;

    private enum OperationControlRequest
    {
        None,
        Cancel,
        Complete,
    }
}
