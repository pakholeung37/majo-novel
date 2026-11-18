#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Dialogue.Presentation;
using Yarn.Markup;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Presentation;

internal sealed partial class MajoActionMarkupBridge : ActionMarkupHandler
{
    private readonly Action requestHurryUp;
    private readonly Action requestNextLine;
    private readonly IReadOnlyList<IDialogueMarkupHandler> handlers;
    private readonly Func<DialoguePresentationOrigin> presentationOrigin;
    private IReadOnlyList<IDialogueMarkupHandler> activeHandlers = Array.Empty<IDialogueMarkupHandler>();
    private DialogueText? currentText;
    private TaskCompletionSource inactive = CompletedInactiveSource();

    public MajoActionMarkupBridge(
        Action requestHurryUp,
        Action requestNextLine,
        IReadOnlyList<IDialogueMarkupHandler> handlers,
        Func<DialoguePresentationOrigin> presentationOrigin)
    {
        this.requestHurryUp = requestHurryUp ??
            throw new ArgumentNullException(nameof(requestHurryUp));
        this.requestNextLine = requestNextLine ??
            throw new ArgumentNullException(nameof(requestNextLine));
        this.handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        this.presentationOrigin = presentationOrigin ?? throw new ArgumentNullException(nameof(presentationOrigin));
    }

    public bool IsLineActive { get; private set; }

    public bool IsDisplayComplete { get; private set; }

    public event Action? DisplayCompleted;

    public event Action? LineDismissed;

    public override void OnPrepareForLine(MarkupParseResult line, RichTextLabel text)
    {
        // Yarn's LinePresenter currently prepares handlers directly and its
        // typewriter prepares the same handler list again. Expose one stable
        // lifecycle callback at the Majo boundary.
        if (IsLineActive)
        {
            return;
        }

        currentText = YarnLineConverter.Convert(line);
        inactive = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IsLineActive = true;
        IsDisplayComplete = false;
        var replay = presentationOrigin() != DialoguePresentationOrigin.Normal;
        activeHandlers = handlers.Where(handler =>
            !replay || handler.ReplayPolicy == DialogueMarkupReplayPolicy.Visual).ToArray();
        foreach (var handler in activeHandlers)
        {
            handler.OnLinePrepared(currentText);
        }
    }

    public override void OnLineDisplayBegin(MarkupParseResult line, RichTextLabel text)
    {
        var converted = currentText ?? YarnLineConverter.Convert(line);
        foreach (var handler in activeHandlers)
        {
            handler.OnLineDisplayBegan(converted);
        }
    }

    public override async YarnTask OnCharacterWillAppear(
        int currentCharacterIndex,
        MarkupParseResult line,
        CancellationToken cancellationToken)
    {
        var converted = currentText ?? YarnLineConverter.Convert(line);
        foreach (var handler in activeHandlers)
        {
            await handler.OnCharacterWillAppearAsync(
                currentCharacterIndex,
                converted,
                cancellationToken);
        }
    }

    public override void OnLineDisplayComplete()
    {
        IsDisplayComplete = true;
        foreach (var handler in activeHandlers)
        {
            handler.OnLineDisplayCompleted();
        }

        DisplayCompleted?.Invoke();
    }

    public override void OnLineWillDismiss()
    {
        foreach (var handler in activeHandlers)
        {
            handler.OnLineWillDismiss();
        }

        LineDismissed?.Invoke();
        ResetState();
    }

    public void Advance()
    {
        if (!IsLineActive)
        {
            return;
        }

        if (IsDisplayComplete)
        {
            requestNextLine();
        }
        else
        {
            requestHurryUp();
        }
    }

    public void ResetState()
    {
        currentText = null;
        activeHandlers = Array.Empty<IDialogueMarkupHandler>();
        IsLineActive = false;
        IsDisplayComplete = false;
        inactive.TrySetResult();
    }

    public Task WaitUntilInactiveAsync(CancellationToken cancellationToken) =>
        IsLineActive ? inactive.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    private static TaskCompletionSource CompletedInactiveSource()
    {
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
