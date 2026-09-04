#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Direction.Execution;

public interface ITransientCueActivity : IAsyncDisposable
{
    Task Settlement { get; }
    bool IsSkippable { get; }
    Task FinishAsync(CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

public sealed class TransientCueActivity : ITransientCueActivity
{
    private readonly IReadOnlyList<IOperationHandle> handles;
    private readonly IDisposable? activityLease;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private int disposed;

    public TransientCueActivity(
        IReadOnlyList<IOperationHandle> handles,
        bool isSkippable = true,
        IDisposable? activityLease = null)
    {
        this.handles = handles ?? Array.Empty<IOperationHandle>();
        IsSkippable = isSkippable;
        this.activityLease = activityLease;
        Settlement = ObserveSettlementAsync();
    }

    public Task Settlement { get; }
    public bool IsSkippable { get; }

    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        List<Exception>? failures = null;
        try
        {
            foreach (var handle in handles)
            {
                try
                {
                    handle.Finish();
                }
                catch (Exception ex)
                {
                    (failures ??= new()).Add(ex);
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await Settlement.WaitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            (failures ??= new()).Add(ex);
        }

        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("Failed to finish transient cue activity.", failures);
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        List<Exception>? failures = null;
        try
        {
            foreach (var handle in handles)
            {
                try
                {
                    handle.Cancel();
                }
                catch (Exception ex)
                {
                    (failures ??= new()).Add(ex);
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await Settlement.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            (failures ??= new()).Add(ex);
        }

        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("Failed to cancel transient cue activity.", failures);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                activityLease?.Dispose();
                foreach (var handle in handles)
                {
                    try
                    {
                        handle.Cancel();
                    }
                    catch
                    {
                        // Ignore error during cancellation cleanup on dispose
                    }
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task ObserveSettlementAsync()
    {
        try
        {
            if (handles.Count > 0)
            {
                await Task.WhenAll(handles.Select(h => h.Completion));
            }
        }
        finally
        {
            activityLease?.Dispose();
        }
    }
}
