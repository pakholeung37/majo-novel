#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.State;

/// <summary>Shared by persistent models of one runtime; never hold a write callback across an await.</summary>
public sealed class StateMutationGate
{
    private readonly object sync = new();
    private readonly Queue<TaskCompletionSource<IDisposable>> waitingWriters = new();
    private readonly List<TaskCompletionSource<IDisposable>> waitingFreezes = new();
    private readonly AsyncLocal<int> writeDepth = new();
    private int freezes;
    private int writers;
    private int pendingFreezes;
    private long revision;

    public long Revision
    {
        get { lock (sync) return revision; }
    }

    public void Write(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (writeDepth.Value != 0)
        {
            mutation();
            return;
        }
        lock (sync)
        {
            if (freezes != 0 || pendingFreezes != 0)
                throw new StateRuntimeException("Persistent state is frozen for capture. Submit this change after the boundary completes.");
            writers++;
        }
        writeDepth.Value++;
        var committed = false;
        try
        {
            mutation();
            committed = true;
        }
        finally
        {
            writeDepth.Value--;
            if (committed)
            {
                lock (sync) revision = checked(revision + 1);
            }
            ReleaseWriter();
        }
    }

    public T Write<T>(Func<T> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        T result = default!;
        Write(() => { result = mutation(); });
        return result;
    }

    /// <summary>
    /// Waits until capture has released the frozen snapshot, then owns a persistent
    /// write across asynchronous preparation and logical commit. Visual settlement
    /// must happen after this callback returns.
    /// </summary>
    public async Task WriteAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (writeDepth.Value != 0)
        {
            await mutation(cancellationToken);
            return;
        }

        using var lease = await AcquireWriterAsync(cancellationToken);
        writeDepth.Value++;
        var committed = false;
        try
        {
            await mutation(cancellationToken);
            committed = true;
        }
        finally
        {
            writeDepth.Value--;
            if (committed)
            {
                lock (sync) revision = checked(revision + 1);
            }
        }
    }

    public async Task<T> WriteAsync<T>(
        Func<CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (writeDepth.Value != 0)
            return await mutation(cancellationToken);

        using var lease = await AcquireWriterAsync(cancellationToken);
        writeDepth.Value++;
        var committed = false;
        try
        {
            var result = await mutation(cancellationToken);
            committed = true;
            return result;
        }
        finally
        {
            writeDepth.Value--;
            if (committed)
            {
                lock (sync) revision = checked(revision + 1);
            }
        }
    }

    public IDisposable Freeze()
    {
        lock (sync)
        {
            if (writers != 0)
                throw new StateRuntimeException("Cannot capture inside a persistent write callback.");
            freezes++;
            return new FreezeLease(this);
        }
    }

    /// <summary>Waits for committed writes and then prevents new writes until disposed.</summary>
    public async ValueTask<IDisposable> FreezeAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<IDisposable>? waiter = null;
        lock (sync)
        {
            if (writeDepth.Value != 0)
                throw new StateRuntimeException("Cannot capture inside a persistent write callback.");
            if (writers == 0)
            {
                freezes++;
                return new FreezeLease(this);
            }

            pendingFreezes++;
            waiter = NewWaiter();
            waitingFreezes.Add(waiter);
        }

        try
        {
            return await waiter.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var granted = false;
            TaskCompletionSource<IDisposable>[] writersToGrant;
            lock (sync)
            {
                if (waitingFreezes.Remove(waiter)) pendingFreezes--;
                else granted = true;
                writersToGrant = TakeWaitingWritersLocked();
            }
            if (granted && waiter.Task.IsCompletedSuccessfully) waiter.Task.Result.Dispose();
            GrantWriters(writersToGrant);
            throw;
        }
    }

    private ValueTask<IDisposable> AcquireWriterAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (freezes == 0 && pendingFreezes == 0)
            {
                writers++;
                return ValueTask.FromResult<IDisposable>(new WriteLease(this));
            }

            var waiter = NewWaiter();
            waitingWriters.Enqueue(waiter);
            return new ValueTask<IDisposable>(WaitForWriterAsync(waiter, cancellationToken));
        }
    }

    private static async Task<IDisposable> WaitForWriterAsync(
        TaskCompletionSource<IDisposable> waiter,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            () => waiter.TrySetCanceled(cancellationToken));
        return await waiter.Task;
    }

    private static TaskCompletionSource<IDisposable> NewWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ReleaseWriter()
    {
        TaskCompletionSource<IDisposable>[] grantedFreezes = Array.Empty<TaskCompletionSource<IDisposable>>();
        TaskCompletionSource<IDisposable>[] grantedWriters = Array.Empty<TaskCompletionSource<IDisposable>>();
        lock (sync)
        {
            writers--;
            if (writers == 0 && pendingFreezes != 0)
            {
                grantedFreezes = waitingFreezes.ToArray();
                waitingFreezes.Clear();
                freezes += grantedFreezes.Length;
                pendingFreezes = 0;
            }
            else
            {
                grantedWriters = TakeWaitingWritersLocked();
            }
        }
        foreach (var waiter in grantedFreezes) waiter.TrySetResult(new FreezeLease(this));
        GrantWriters(grantedWriters);
    }

    private void ReleaseFreeze()
    {
        TaskCompletionSource<IDisposable>[] grantedWriters;
        lock (sync)
        {
            freezes--;
            grantedWriters = TakeWaitingWritersLocked();
        }
        GrantWriters(grantedWriters);
    }

    private TaskCompletionSource<IDisposable>[] TakeWaitingWritersLocked()
    {
        if (freezes != 0 || pendingFreezes != 0 || waitingWriters.Count == 0)
            return Array.Empty<TaskCompletionSource<IDisposable>>();
        var granted = new List<TaskCompletionSource<IDisposable>>(waitingWriters.Count);
        while (waitingWriters.TryDequeue(out var waiter))
        {
            if (waiter.Task.IsCompleted) continue;
            writers++;
            granted.Add(waiter);
        }
        return granted.ToArray();
    }

    private void GrantWriters(IEnumerable<TaskCompletionSource<IDisposable>> waiters)
    {
        foreach (var waiter in waiters)
        {
            if (!waiter.TrySetResult(new WriteLease(this))) ReleaseWriter();
        }
    }

    private sealed class WriteLease(StateMutationGate owner) : IDisposable
    {
        private StateMutationGate? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseWriter();
    }

    private sealed class FreezeLease(StateMutationGate owner) : IDisposable
    {
        private StateMutationGate? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseFreeze();
    }
}

/// <summary>Every persistent write path, including backing stores, must use the bound gate.</summary>
public interface IStateMutationParticipant
{
    void BindStateMutationGate(StateMutationGate gate);
}

/// <summary>
/// Marker for a contributor whose writes are exclusively owned by the line-boundary
/// protocol itself. General-purpose plugin contributors must use
/// <see cref="IStateMutationParticipant"/> instead.
/// </summary>
public interface ILineBoundaryOwnedStateContributor;
