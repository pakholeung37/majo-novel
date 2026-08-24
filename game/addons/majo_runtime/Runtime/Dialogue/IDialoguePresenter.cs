#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue;

public sealed class DialoguePresentationContext
{
    private readonly Action requestHurryUp;
    private readonly Action requestNextLine;

    public DialoguePresentationContext(
        CancellationToken hurryUpToken,
        CancellationToken nextLineToken,
        Action requestHurryUp,
        Action requestNextLine)
    {
        HurryUpToken = hurryUpToken;
        NextLineToken = nextLineToken;
        this.requestHurryUp = requestHurryUp ??
            throw new ArgumentNullException(nameof(requestHurryUp));
        this.requestNextLine = requestNextLine ??
            throw new ArgumentNullException(nameof(requestNextLine));
    }

    public CancellationToken HurryUpToken { get; }

    public CancellationToken NextLineToken { get; }

    public void RequestHurryUp() => requestHurryUp();

    public void RequestNextLine() => requestNextLine();
}

public interface IDialoguePresenter
{
    Task OnDialogueStartedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task PresentLineAsync(
        DialogueLine line,
        DialoguePresentationContext context) => Task.CompletedTask;

    Task<DialogueOption?> PresentOptionsAsync(
        IReadOnlyList<DialogueOption> options,
        CancellationToken cancellationToken) => Task.FromResult<DialogueOption?>(null);

    Task OnDialogueCompletedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    void OnNodeEntered(string nodeName)
    {
    }

    void OnNodeExited(string nodeName)
    {
    }
}

public interface IDialoguePresentationSettings
{
    int LettersPerSecond { get; set; }
}

public sealed class DialoguePresenterRegistry
{
    private readonly List<IDialoguePresenter> presenters = new();

    public IReadOnlyList<IDialoguePresenter> Presenters => presenters;

    public DialoguePresenterRegistry Register(IDialoguePresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        if (presenters.Contains(presenter))
        {
            throw new DialogueException(
                $"Dialogue presenter '{presenter.GetType().FullName}' is already registered.");
        }

        presenters.Add(presenter);
        return this;
    }
}
