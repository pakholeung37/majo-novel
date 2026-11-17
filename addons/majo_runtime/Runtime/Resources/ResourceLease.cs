#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Resources;

internal sealed class ResourceLease<TResource> : IResourceLease<TResource>
    where TResource : class
{
    private readonly object gate = new();
    private readonly object cancellationGate = new();
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken cancellationToken;
    private readonly Func<CancellationToken, Task<ResourceLeaseAcquisition<TResource>>> acquire;
    private readonly Action<IResourceLease> forget;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ResourceLeaseAcquisition<TResource>? acquisition;
    private bool forgotten;
    private bool cancellationDisposed;

    public ResourceLease(
        ResourceKey logicalKey,
        Func<CancellationToken, Task<ResourceLeaseAcquisition<TResource>>> acquire,
        Action<IResourceLease> forget,
        CancellationToken cancellationToken)
    {
        LogicalKey = logicalKey;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.cancellationToken = cancellation.Token;
        this.acquire = acquire;
        this.forget = forget;
    }

    public string LeaseId { get; } = Guid.NewGuid().ToString("N");
    public ResourceKey LogicalKey { get; }
    public string? ResolvedPath { get; private set; }
    public Type ResourceType => typeof(TResource);
    public ResourceLeaseState State { get; private set; } = ResourceLeaseState.Loading;
    public TResource? Resource { get; private set; }
    object? IResourceLease.Resource => Resource;
    public Exception? Failure { get; private set; }
    public Task Completion => completion.Task;

    public void Start() => _ = RunAsync();

    public void Cancel() => Terminate(ResourceLeaseState.Cancelled);
    public void Release() => Terminate(ResourceLeaseState.Released);
    public void Dispose() => Release();

    private void Terminate(ResourceLeaseState terminal)
    {
        ResourceLeaseAcquisition<TResource>? retained;
        lock (gate)
        {
            if (State is ResourceLeaseState.Cancelled or ResourceLeaseState.Released or ResourceLeaseState.Failed)
                return;
            State = terminal;
            retained = acquisition;
            acquisition = null;
            Resource = null;
        }
        try { DisposeCancellation(cancel: true); }
        finally
        {
            try { if (retained is not null) retained.Release(); }
            finally
            {
                completion.TrySetCanceled(cancellationToken);
                ForgetOnce();
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            var acquired = await acquire(cancellationToken);
            lock (gate)
            {
                if (State != ResourceLeaseState.Loading)
                {
                    acquired.Release();
                    return;
                }
                acquisition = acquired;
                Resource = acquired.Resource;
                ResolvedPath = acquired.ResolvedPath;
                State = ResourceLeaseState.Ready;
            }
            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
                if (State == ResourceLeaseState.Loading) State = ResourceLeaseState.Cancelled;
            completion.TrySetCanceled(cancellationToken);
            ForgetOnce();
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (State == ResourceLeaseState.Loading)
                {
                    Failure = exception;
                    State = ResourceLeaseState.Failed;
                }
            }
            completion.TrySetException(exception);
            _ = completion.Task.Exception;
            ForgetOnce();
        }
        finally
        {
            // Acquisition is finished even while a ready lease retains its
            // resource. No caller cancellation registration is needed then.
            DisposeCancellation(cancel: false);
        }
    }

    private void DisposeCancellation(bool cancel)
    {
        lock (cancellationGate)
        {
            if (cancellationDisposed) return;
            // Claim cleanup before Cancel: cancellation can synchronously
            // resume RunAsync, whose finally also requests this cleanup.
            cancellationDisposed = true;
        }
        // Cancellation callbacks may reenter the lease on another thread.
        // Only claim ownership under the lock; invoke them outside it.
        try { if (cancel) cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }

    private void ForgetOnce()
    {
        lock (gate)
        {
            if (forgotten) return;
            forgotten = true;
        }
        forget(this);
    }
}

internal sealed record ResourceLeaseAcquisition<TResource>(
    TResource Resource, string ResolvedPath, Action Release) where TResource : class;
