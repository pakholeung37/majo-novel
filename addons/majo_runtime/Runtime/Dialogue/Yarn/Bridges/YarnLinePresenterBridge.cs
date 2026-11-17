#nullable enable

// Portions adapted from YarnSpinner-Godot (MIT), copyright 2020 Secret Lab Pty Ltd
// and Yarn Spinner Contributors. See YarnSpinner-LICENSE.md in this directory.

using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using YarnSpinnerGodot;
using YarnDialogueOption = YarnSpinnerGodot.DialogueOption;

namespace Majo.Runtime.Dialogue.Yarn.Bridges;

/// <summary>Owns Majo panel visibility while reusing the upstream view and typewriter.</summary>
internal sealed partial class YarnLinePresenterBridge(LinePresenter view) : Node, DialoguePresenterBase
{
    private const ulong HideDebounceMilliseconds = 50;
    private ulong? pendingHideDeadline;
    private Tween? hideTween;

    public List<IActionMarkupHandler> ActionMarkupHandlers => view.ActionMarkupHandlers;

    public void ResetVisibility()
    {
        CancelPendingHide();
        if (IsInstanceValid(view.presenterControl)) view.presenterControl.Visible = false;
    }

    private void CancelPendingHide()
    {
        pendingHideDeadline = null;
        hideTween?.Kill();
        hideTween = null;
    }

    public override void _ExitTree() => CancelPendingHide();

    public override void _Process(double delta)
    {
        if (pendingHideDeadline is not ulong deadline || Time.GetTicksMsec() < deadline)
            return;
        pendingHideDeadline = null;
        if (!IsInstanceValid(view.presenterControl)) return;
        if (view.useFadeEffect && view.fadeDownDuration > 0)
        {
            hideTween = CreateTween();
            hideTween.TweenProperty(view.presenterControl, "modulate:a", 0f, view.fadeDownDuration);
            hideTween.TweenCallback(Callable.From(ResetVisibility));
        }
        else ResetVisibility();
    }

    public YarnTask OnDialogueStartedAsync()
    {
        ResetVisibility();
        return YarnTask.CompletedTask;
    }

    public YarnTask OnDialogueCompleteAsync()
    {
        ResetVisibility();
        return YarnTask.CompletedTask;
    }

    public YarnTask<YarnDialogueOption?> RunOptionsAsync(YarnDialogueOption[] options, CancellationToken token)
    {
        ResetVisibility();
        return YarnTask<YarnDialogueOption?>.FromResult(null);
    }

    public async YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token)
    {
        if (!IsInstanceValid(view.lineText))
            throw new DialogueException("Dialogue LinePresenter requires a text view.");

        var panel = view.presenterControl;
        var continuingPanel = IsInstanceValid(panel) && panel.Visible;
        CancelPendingHide();
        var text = !IsInstanceValid(view.characterNameText) && view.showCharacterNameInLineView
            ? line.Text : line.TextWithoutCharacterName;
        if (IsInstanceValid(view.characterNameText) && IsInstanceValid(view.characterNameContainer))
        {
            view.characterNameContainer.Visible = !string.IsNullOrWhiteSpace(line.CharacterName);
            view.characterNameText.Text = line.CharacterName ?? string.Empty;
        }
        foreach (var processor in ActionMarkupHandlers)
            processor.OnPrepareForLine(text, view.lineText);
        view.Typewriter ??= new InstantTypewriter
        {
            ActionMarkupHandlers = ActionMarkupHandlers,
            TextElement = view.lineText,
            ConvertHTMLToBBCode = view.ConvertHTMLToBBCode,
        };
        var typewriter = view.Typewriter;
        typewriter.PrepareForContent(text);
        if (IsInstanceValid(panel))
        {
            panel.Visible = true;
            if (view.useFadeEffect)
            {
                await Effects.FadeAlphaAsync(panel, continuingPanel ? panel.Modulate.A : 0, 1,
                    continuingPanel && panel.Modulate.A >= 1 ? 0 : view.fadeUpDuration, token.HurryUpToken);
                if (!IsInstanceValid(this)) return;
            }
            else panel.Modulate = panel.Modulate with { A = 1 };
        }
        await typewriter.RunTypewriter(text, token.HurryUpToken).SuppressCancellationThrow();
        if (view.autoAdvance)
            await YarnTask.Delay((int)(view.autoAdvanceDelay * 1000), token.NextLineToken).SuppressCancellationThrow();
        else
            await YarnTask.WaitUntilCanceled(token.NextLineToken).SuppressCancellationThrow();
        if (!IsInstanceValid(this)) return;
        typewriter.ContentWillDismiss();
        // Release the Runner immediately so the next line can cancel this hide.
        pendingHideDeadline = Time.GetTicksMsec() + HideDebounceMilliseconds;
        typewriter.ContentDidDismiss();
    }
}
