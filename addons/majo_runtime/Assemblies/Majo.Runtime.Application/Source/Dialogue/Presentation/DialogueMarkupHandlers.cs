#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Presentation;

public enum DialogueMarkupReplayPolicy
{
    Visual = 0,
    RunOnce = 1,
}

public interface IDialogueMarkupHandler
{
    DialogueMarkupReplayPolicy ReplayPolicy { get; }

    void OnLinePrepared(DialogueText text)
    {
    }

    void OnLineDisplayBegan(DialogueText text)
    {
    }

    Task OnCharacterWillAppearAsync(
        int characterIndex,
        DialogueText text,
        CancellationToken cancellationToken) => Task.CompletedTask;

    void OnLineDisplayCompleted()
    {
    }

    void OnLineWillDismiss()
    {
    }
}

public sealed class DialogueMarkupHandlerRegistry
{
    private readonly List<IDialogueMarkupHandler> handlers = new();

    public IReadOnlyList<IDialogueMarkupHandler> Handlers => handlers;

    public DialogueMarkupHandlerRegistry Register(IDialogueMarkupHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!Enum.IsDefined(handler.ReplayPolicy))
            throw new DialogueException(
                $"Dialogue markup handler '{handler.GetType().FullName}' has an invalid replay policy.");
        if (handlers.Contains(handler))
        {
            throw new DialogueException(
                $"Dialogue markup handler '{handler.GetType().FullName}' is already registered.");
        }

        handlers.Add(handler);
        return this;
    }
}
