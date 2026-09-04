#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Operations;

public sealed class OperationDefinition
{
    public OperationDefinition(
        Func<OperationContext, Task> executeAsync,
        Func<CancellationToken, Task>? onFinish = null,
        Func<CancellationToken, Task>? onCancel = null)
    {
        ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        FinishAsync = onFinish ?? (_ => Task.CompletedTask);
        CancelAsync = onCancel ?? (_ => Task.CompletedTask);
    }

    public Func<OperationContext, Task> ExecuteAsync { get; }

    public Func<CancellationToken, Task> FinishAsync { get; }

    public Func<CancellationToken, Task> CancelAsync { get; }
}
