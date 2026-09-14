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
    public async Task PresentLineAsync(DialogueLine line, DialoguePresentationContext context)
    {
        if (string.IsNullOrWhiteSpace(line.Speaker)) return;
        var speaker = line.Speaker;
        registry.SetSpeaking(speaker, true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.NextLineToken);
        }
        catch (OperationCanceledException) when (context.NextLineToken.IsCancellationRequested)
        {
        }
        finally
        {
            registry.SetSpeaking(speaker, false);
        }
    }

    public Task<DialogueOption?> PresentOptionsAsync(
        IReadOnlyList<DialogueOption> options, CancellationToken cancellationToken) =>
        Task.FromResult<DialogueOption?>(null);

    public Task OnDialogueCompletedAsync(CancellationToken cancellationToken)
    {
        registry.StopAll();
        return Task.CompletedTask;
    }
}
