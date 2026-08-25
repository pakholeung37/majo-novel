#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Operations;

public sealed class OperationDefinition
{
    public OperationDefinition(
        Func<OperationContext, Task> executeAsync,
        string? id = null,
        string? groupId = null,
        Func<CancellationToken, Task>? completeAsync = null,
        Func<CancellationToken, Task>? cancelAsync = null,
        bool isSkippable = false)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        ValidateOptionalName(id, nameof(id));
        ValidateOptionalName(groupId, nameof(groupId));

        ExecuteAsync = executeAsync;
        Id = id;
        GroupId = groupId;
        CompleteAsync = completeAsync ?? (_ => Task.CompletedTask);
        CancelAsync = cancelAsync ?? (_ => Task.CompletedTask);
        IsSkippable = isSkippable;
    }

    public string? Id { get; }

    public string? GroupId { get; }

    public Func<OperationContext, Task> ExecuteAsync { get; }

    public Func<CancellationToken, Task> CompleteAsync { get; }

    public Func<CancellationToken, Task> CancelAsync { get; }

    public bool IsSkippable { get; }

    private static void ValidateOptionalName(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Operation names cannot be empty or whitespace.", parameterName);
        }
    }
}
