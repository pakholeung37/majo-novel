#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Presentation;

public interface IDialogueMarkupHandler
{
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
        if (handlers.Contains(handler))
        {
            throw new DialogueException(
                $"Dialogue markup handler '{handler.GetType().FullName}' is already registered.");
        }

        handlers.Add(handler);
        return this;
    }
}
