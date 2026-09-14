#nullable enable

using System;
using System.Threading;
using Godot;
using Yarn.Markup;
using YarnSpinnerGodot;

namespace Majo.Demos.AceAttorney;

/// <summary>Plays one short blip as each non-space glyph is revealed.</summary>
public sealed class DialogueBlipHandler(AudioStreamPlayer player) : IActionMarkupHandler
{
    private ulong lastBlipTime;
    public int PlayCount { get; private set; }

    public void OnPrepareForLine(MarkupParseResult line, RichTextLabel text) =>
        lastBlipTime = 0;

    public void OnLineDisplayBegin(MarkupParseResult line, RichTextLabel text) { }

    public YarnTask OnCharacterWillAppear(
        int currentCharacterIndex, MarkupParseResult line, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested &&
            currentCharacterIndex < line.Text.Length &&
            !char.IsWhiteSpace(line.Text[currentCharacterIndex]))
        {
            var now = Time.GetTicksMsec();
            // Guard against a burst when typewriter catch-up reveals several glyphs at once.
            if (lastBlipTime == 0 || now - lastBlipTime >= 20)
            {
                player.Play();
                PlayCount++;
                lastBlipTime = now;
            }
        }
        return YarnTask.CompletedTask;
    }

    public void OnLineDisplayComplete() { }

    public void OnLineWillDismiss() => player.Stop();
}
