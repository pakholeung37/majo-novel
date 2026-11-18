#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Commands;

public interface IDialogueNodeAccessor : IRuntimeService
{
    string? CurrentNode { get; }
}

public sealed class DelegateDialogueNodeAccessor : IDialogueNodeAccessor
{
    private readonly Func<string?> currentNode;

    public DelegateDialogueNodeAccessor(Func<string?> currentNode)
    {
        this.currentNode = currentNode ?? throw new ArgumentNullException(nameof(currentNode));
    }

    public string? CurrentNode => currentNode();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class DialogueNodeAccessorExtensions
{
    public static string RequireNode(this IDialogueNodeAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        var node = accessor.CurrentNode;
        if (string.IsNullOrWhiteSpace(node))
        {
            throw new Direction.Core.DirectionReferenceException(
                "Direction symbols cannot be resolved outside a Yarn Node.");
        }

        return node;
    }
}
