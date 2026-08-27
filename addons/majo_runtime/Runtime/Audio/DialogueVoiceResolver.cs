#nullable enable

using System;
using System.Linq;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Audio;

public static class DialogueVoiceResolver
{
    public sealed record VoiceResolution(
        string VoiceId,
        string ResolvedPath,
        string? ResolvedLocale,
        LocalizedResourceFallbackSource FallbackSource);

    public static VoiceResolution? ResolveExistingVoice(
        DialogueLine line,
        IAudioManager audio,
        ILocaleManager locales,
        ILocalizedResourceResolver resources)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(locales);
        ArgumentNullException.ThrowIfNull(resources);
        var voiceId = ResolveVoiceId(line);
        if (voiceId is null)
        {
            return null;
        }

        var resolution = resources.ResolvePath(
            voiceId,
            locales.VoiceLocale,
            locales.GetVoiceFallbackChain(locales.VoiceLocale),
            candidate => audio.VoiceLoader.Exists(candidate));
        return resolution.ResolvedPath is null
            ? null
            : new VoiceResolution(
                voiceId,
                resolution.ResolvedPath,
                resolution.ResolvedLocale,
                resolution.FallbackSource);
    }

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

        var locale = audio.VoiceLocale;
        var localizedId = $"{locale}/{voiceId}";
        return audio.VoiceLoader.Exists(localizedId) || audio.VoiceLoader.Exists(voiceId) ? voiceId : null;
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

        var voiceId = overrideIds.SingleOrDefault() ?? NormalizeLineId(line.Id);
        if (voiceId.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return voiceId;
    }

    private static string NormalizeLineId(string lineId) =>
        lineId.StartsWith("line:", StringComparison.OrdinalIgnoreCase)
            ? lineId["line:".Length..]
            : lineId;
}
