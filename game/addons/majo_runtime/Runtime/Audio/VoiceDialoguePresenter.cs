#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue;

namespace Majo.Runtime.Audio;

public sealed class VoiceDialoguePresenter : IDialoguePresenter
{
    public const string MetadataPrefix = "voice:";
    private readonly IAudioManager audio;

    public VoiceDialoguePresenter(IAudioManager audio)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    public string? LastVoiceId { get; private set; }

    public async Task PresentLineAsync(
        DialogueLine line,
        DialoguePresentationContext context)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(context);
        await audio.StopVoiceAsync(cancellationToken: context.NextLineToken);
        var voiceId = DialogueVoiceResolver.ResolveExistingVoiceId(line, audio);
        if (voiceId is null)
        {
            LastVoiceId = null;
            return;
        }

        var result = await audio.PlayVoiceAsync(
            voiceId,
            cancellationToken: context.NextLineToken);
        LastVoiceId = result.IsSkipped ? null : voiceId;
    }

    public async Task OnDialogueCompletedAsync(CancellationToken cancellationToken)
    {
        await audio.StopVoiceAsync(cancellationToken: cancellationToken);
        LastVoiceId = null;
    }
}
