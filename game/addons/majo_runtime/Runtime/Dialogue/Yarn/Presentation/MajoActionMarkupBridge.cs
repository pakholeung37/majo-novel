#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Majo.Runtime.Dialogue.Presentation;
using Yarn.Markup;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.Presentation;

internal sealed partial class MajoActionMarkupBridge : ActionMarkupHandler
{
    private readonly DialogueRunner runner;
    private readonly IReadOnlyList<IDialogueMarkupHandler> handlers;
    private DialogueText? currentText;

    public MajoActionMarkupBridge(
        DialogueRunner runner,
        IReadOnlyList<IDialogueMarkupHandler> handlers)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    public bool IsLineActive { get; private set; }

    public bool IsDisplayComplete { get; private set; }

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
        IsLineActive = true;
        IsDisplayComplete = false;
        foreach (var handler in handlers)
        {
            handler.OnLinePrepared(currentText);
        }
    }

    public override void OnLineDisplayBegin(MarkupParseResult line, RichTextLabel text)
    {
        var converted = currentText ?? YarnLineConverter.Convert(line);
        foreach (var handler in handlers)
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
        foreach (var handler in handlers)
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
        foreach (var handler in handlers)
        {
            handler.OnLineDisplayCompleted();
        }
    }

    public override void OnLineWillDismiss()
    {
        foreach (var handler in handlers)
        {
            handler.OnLineWillDismiss();
        }

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
            runner.RequestNextLine();
        }
        else
        {
            runner.RequestHurryUpLine();
        }
    }

    public void ResetState()
    {
        currentText = null;
        IsLineActive = false;
        IsDisplayComplete = false;
    }
}
