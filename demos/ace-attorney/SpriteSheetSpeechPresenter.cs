#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Demos.AceAttorney.Actors;
using Majo.Runtime.Dialogue;

namespace Majo.Demos.AceAttorney;

public sealed class SpriteSheetSpeechPresenter(SpriteSheetActorRegistry registry) : IDialoguePresenter
{
    private string? activeSpeaker;
    private long lineVersion;

    public async Task PresentLineAsync(DialogueLine line, DialoguePresentationContext context)
    {
        StopSpeaking();
        var version = ++lineVersion;
        activeSpeaker = string.IsNullOrWhiteSpace(line.Speaker) ? null : line.Speaker;
        if (activeSpeaker is not null) registry.SetSpeaking(activeSpeaker, true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.NextLineToken);
        }
        catch (OperationCanceledException) when (context.NextLineToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (version == lineVersion) StopSpeaking();
        }
    }

    public void OnLineDisplayCompleted() => StopSpeaking();

    public Task<DialogueOption?> PresentOptionsAsync(
        IReadOnlyList<DialogueOption> options, CancellationToken cancellationToken) =>
        Task.FromResult<DialogueOption?>(null);

    public Task OnDialogueCompletedAsync(CancellationToken cancellationToken)
    {
        StopSpeaking();
        registry.StopAll();
        return Task.CompletedTask;
    }

    private void StopSpeaking()
    {
        if (activeSpeaker is not { } speaker) return;
        activeSpeaker = null;
        registry.SetSpeaking(speaker, false);
    }
}
