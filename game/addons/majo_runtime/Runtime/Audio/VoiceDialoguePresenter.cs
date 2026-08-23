#nullable enable

using System;
using System.Linq;
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
        var overrideIds = line.Metadata
            .Where(metadata => metadata.StartsWith(
                MetadataPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(metadata => metadata[MetadataPrefix.Length..].Trim())
            .ToArray();
        if (overrideIds.Length > 1)
        {
            throw new AudioException(
                $"Dialogue line '{line.Id}' contains multiple voice metadata overrides.");
        }

        await audio.StopVoiceAsync(cancellationToken: context.NextLineToken);
        var voiceId = overrideIds.SingleOrDefault() ?? line.Id;
        if (voiceId.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            LastVoiceId = null;
            return;
        }

        var localizedId = $"{audio.VoiceLocale}/{voiceId}";
        var isStrictOverride = overrideIds.Length == 1;
        if (!audio.VoiceLoader.Exists(localizedId))
        {
            if (isStrictOverride)
            {
                throw new AudioException(
                    $"Strict voice override '{voiceId}' for line '{line.Id}' does not exist " +
                    $"for locale '{audio.VoiceLocale}'.");
            }

            LastVoiceId = null;
            return;
        }

        await audio.PlayVoiceAsync(
            voiceId,
            cancellationToken: context.NextLineToken);
        LastVoiceId = voiceId;
    }

    public async Task OnDialogueCompletedAsync(CancellationToken cancellationToken)
    {
        await audio.StopVoiceAsync(cancellationToken: cancellationToken);
        LastVoiceId = null;
    }
}
