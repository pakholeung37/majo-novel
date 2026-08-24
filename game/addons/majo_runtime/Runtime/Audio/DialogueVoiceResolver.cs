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
        var voiceId = ResolveVoiceId(line);
        if (voiceId is null)
        {
            return null;
        }

        var localizedId = $"{audio.VoiceLocale}/{voiceId}";
        return audio.VoiceLoader.Exists(localizedId) ? voiceId : null;
    }

    public static string? ResolveVoiceId(DialogueLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
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

        return voiceId;
    }
}
