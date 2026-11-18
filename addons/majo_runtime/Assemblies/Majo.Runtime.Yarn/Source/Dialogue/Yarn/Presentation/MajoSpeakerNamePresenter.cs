#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Majo.Runtime.Localization;
using YarnSpinnerGodot;
using YarnDialogueOption = YarnSpinnerGodot.DialogueOption;

namespace Majo.Runtime.Dialogue.Yarn.Presentation;

internal sealed partial class MajoSpeakerNamePresenter : Node, DialoguePresenterBase
{
    private const ulong HideDebounceMilliseconds = 50;
    private ulong? pendingHideDeadline;
    private int presentationVersion;
    private readonly CanvasItem container;
    private readonly RichTextLabel text;
    private readonly ILocaleManager? localeManager;
    private readonly DialogueSpeakerNameResolver? resolver;

    public MajoSpeakerNamePresenter(
        CanvasItem container,
        RichTextLabel text,
        ILocaleManager? localeManager,
        DialogueSpeakerNameResolver? resolver)
    {
        this.container = container ?? throw new ArgumentNullException(nameof(container));
        this.text = text ?? throw new ArgumentNullException(nameof(text));
        this.localeManager = localeManager;
        this.resolver = resolver;
    }

    public List<IActionMarkupHandler> ActionMarkupHandlers { get; } = new();

    public YarnTask OnDialogueStartedAsync()
    {
        Hide();
        return YarnTask.CompletedTask;
    }

    public async YarnTask RunLineAsync(LocalizedLine line, LineCancellationToken token)
    {
        pendingHideDeadline = null;
        var version = ++presentationVersion;
        var speaker = line.CharacterName;
        if (!string.IsNullOrWhiteSpace(speaker) && resolver is not null)
        {
            var locale = localeManager?.DialogueLocale ?? TranslationServer.GetLocale();
            speaker = resolver(speaker, locale) ?? speaker;
        }

        if (GodotObject.IsInstanceValid(container) && GodotObject.IsInstanceValid(text))
        {
            text.Text = speaker ?? string.Empty;
            container.Visible = !string.IsNullOrWhiteSpace(speaker);
        }

        try
        {
            await YarnTask.WaitUntilCanceled(token.NextLineToken).SuppressCancellationThrow();
        }
        finally
        {
            // Do not await the buffer: Yarn must be able to start the next line,
            // which cancels this hide before it can flash the speaker panel.
            if (version == presentationVersion)
            {
                pendingHideDeadline = Time.GetTicksMsec() + HideDebounceMilliseconds;
            }
        }
    }

    public YarnTask<YarnDialogueOption?> RunOptionsAsync(
        YarnDialogueOption[] dialogueOptions,
        CancellationToken cancellationToken)
    {
        Hide();
        return YarnTask<YarnDialogueOption?>.FromResult(null);
    }

    public YarnTask OnDialogueCompleteAsync()
    {
        Hide();
        return YarnTask.CompletedTask;
    }

    public void Reset() => Hide();

    public override void _Process(double delta)
    {
        if (pendingHideDeadline is ulong deadline && Time.GetTicksMsec() >= deadline)
        {
            Hide();
        }
    }

    public override void _ExitTree()
    {
        pendingHideDeadline = null;
        presentationVersion++;
    }

    private void Hide()
    {
        pendingHideDeadline = null;
        presentationVersion++;
        if (GodotObject.IsInstanceValid(container))
        {
            container.Visible = false;
        }
    }
}
