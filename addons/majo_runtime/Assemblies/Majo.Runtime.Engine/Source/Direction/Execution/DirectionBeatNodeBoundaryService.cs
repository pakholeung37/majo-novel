#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;

namespace Majo.Runtime.Direction.Execution;

public sealed class DirectionBeatNodeBoundaryService : IRuntimeService
{
    private readonly IDialogueBackend dialogue;
    private readonly IDirectionBeatRuntime beats;
    private bool subscribed;

    public DirectionBeatNodeBoundaryService(
        IDialogueBackend dialogue,
        IDirectionBeatRuntime beats)
    {
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!subscribed)
        {
            dialogue.NodeExited += HandleNodeExited;
            dialogue.Failed += HandleDialogueFailed;
            subscribed = true;
        }
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        beats.AbortOpen();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (subscribed)
        {
            dialogue.NodeExited -= HandleNodeExited;
            dialogue.Failed -= HandleDialogueFailed;
            subscribed = false;
        }
        return Task.CompletedTask;
    }

    private void HandleNodeExited(string nodeId) => beats.ValidateNodeBoundary(nodeId);

    private void HandleDialogueFailed(Exception _) => beats.AbortOpen();
}
