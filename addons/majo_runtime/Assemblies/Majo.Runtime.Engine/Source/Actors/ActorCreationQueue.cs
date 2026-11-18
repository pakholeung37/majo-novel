#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Actors;

internal sealed class ActorCreationQueue : IDisposable
{
    internal sealed record Pending(Task<IActor> Task, string FactoryId, string DefinitionId, Task Settled);
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private CancellationTokenSource lifetime = new();
    private bool accepting = true;
    private bool disposed;
    public int Count { get { lock (gate) return pending.Count; } }
    public bool TryGet(string id, out Pending entry) { lock (gate) return pending.TryGetValue(id, out entry!); }

    public Task<IActor> Start(string id, ActorDefinition definition, Func<CancellationToken, Task<IActor>> create)
    {
        var completion = new TaskCompletionSource<IActor>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Pending(completion.Task, definition.FactoryId, definition.DefinitionId, settled.Task);
        CancellationToken token;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!accepting) throw new ActorException("Actor creation is suspended until lifecycle cleanup completes.");
            token = lifetime.Token;
            pending.Add(id, entry);
        }
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            IActor? result = null;
            Exception? failure = null;
            var cancelled = false;
            try { result = await create(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
            catch (Exception error) { failure = error; }
            finally
            {
                lock (gate)
                    if (pending.TryGetValue(id, out var current) && ReferenceEquals(current, entry)) pending.Remove(id);
                if (cancelled) completion.TrySetCanceled(token);
                else if (failure is not null)
                {
                    completion.TrySetException(failure);
                    _ = completion.Task.Exception;
                }
                else completion.TrySetResult(result!);
                settled.TrySetResult();
            }
        }
    }

    public async Task<Exception?> CancelAndDrainAsync(CancellationToken token)
    {
        Task[] workers;
        CancellationTokenSource current;
        lock (gate)
        {
            accepting = false;
            current = lifetime;
            workers = pending.Values.Select(entry => entry.Settled).ToArray();
        }
        Exception? cancellationFailure = null;
        try { current.Cancel(); }
        catch (Exception failure) { cancellationFailure = failure; }
        foreach (var worker in workers) await worker.WaitAsync(token);
        return cancellationFailure;
    }

    public void Resume()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending.Count != 0) throw new ActorException("Pending actor cleanup has not completed.");
            lifetime.Dispose();
            lifetime = new CancellationTokenSource();
            accepting = true;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource current;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            accepting = false;
            current = lifetime;
        }
        try { current.Cancel(); }
        finally { current.Dispose(); }
    }
}
