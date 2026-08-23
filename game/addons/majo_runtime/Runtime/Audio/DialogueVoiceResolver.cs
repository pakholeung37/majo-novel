#nullable enable

using System;
using System.Linq;
using Majo.Runtime.Dialogue;

namespace Majo.Runtime.Audio;

internal static class DialogueVoiceResolver
{
    public static string? ResolveExistingVoiceId(
        DialogueLine line,
        IAudioManager audio)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(audio);
        var overrideIds = line.Metadata
            .Where(metadata => metadata.StartsWith(
                VoiceDialoguePresenter.MetadataPrefix,
                StringComparison.OrdinalIgnoreCase))
            .Select(metadata => metadata[VoiceDialoguePresenter.MetadataPrefix.Length..].Trim())
            .ToArray();
        if (overrideIds.Length > 1)
        {
            throw new AudioException(
                $"Dialogue line '{line.Id}' contains multiple voice metadata overrides.");
        }

        var voiceId = overrideIds.SingleOrDefault() ?? line.Id;
        if (voiceId.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var localizedId = $"{audio.VoiceLocale}/{voiceId}";
        if (audio.VoiceLoader.Exists(localizedId))
        {
            return voiceId;
        }

        if (overrideIds.Length == 1)
        {
            throw new AudioException(
                $"Strict voice override '{voiceId}' for line '{line.Id}' does not exist " +
                $"for locale '{audio.VoiceLocale}'.");
        }

        return null;
    }
}
