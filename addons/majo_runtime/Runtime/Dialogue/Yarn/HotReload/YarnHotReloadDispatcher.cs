#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

internal sealed class YarnHotReloadDispatcher
{
    private readonly SynchronizationContext? context;

    public YarnHotReloadDispatcher(SynchronizationContext? context) => this.context = context;

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        context.Post(_ =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task;
    }

    public Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        context.Post(async _ =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task;
    }
}
