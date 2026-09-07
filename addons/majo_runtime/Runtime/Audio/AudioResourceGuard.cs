#nullable enable

using System;
using Majo.Runtime.Localization;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Audio;

internal sealed class AudioResourceGuard
{
    private readonly IMissingResourceReporter? missingResources;
    private readonly bool failOnMissingClips;
    private readonly ILocaleManager? locales;
    private readonly ILocalizedResourceResolver? localizedResources;
    private string voiceLocale;

    public AudioResourceGuard(
        AudioSettings settings,
        IMissingResourceReporter? missingResources = null,
        ILocaleManager? locales = null,
        ILocalizedResourceResolver? localizedResources = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.missingResources = missingResources;
        this.locales = locales;
        this.localizedResources = localizedResources;
        failOnMissingClips = settings.FailOnMissingClips;
        voiceLocale = RequireName(settings.VoiceLocale, nameof(settings.VoiceLocale));
    }

    public string VoiceLocale
    {
        get => voiceLocale;
        set => voiceLocale = RequireName(value, nameof(value));
    }

    public void Initialize()
    {
        if (locales is not null)
        {
            VoiceLocale = locales.VoiceLocale;
            locales.VoiceLocaleChanged += HandleVoiceLocaleChanged;
        }
    }

    public void Shutdown()
    {
        if (locales is not null)
        {
            locales.VoiceLocaleChanged -= HandleVoiceLocaleChanged;
        }
    }

    public bool ShouldSkipMissing(
        IAudioClipLoader loader,
        string clipId,
        string? overridePath = null) =>
        !failOnMissingClips && !loader.Exists(clipId, overridePath);

    public void ReportMissingAudio(
        AudioCategory category,
        string clipId,
        IAudioClipLoader loader,
        string? overridePath = null)
    {
        var requestedPath = overridePath ?? (string.IsNullOrWhiteSpace(loader.PathPrefix)
            ? clipId
            : $"{loader.PathPrefix.TrimEnd('/')}/{clipId.TrimStart('/')}");
        missingResources?.Report(
            category.ToString(),
            clipId,
            "skip",
            requestedPath);
    }

    public string ResolveLocalizedVoiceClipId(string clipId, IAudioClipLoader voiceLoader)
    {
        if (localizedResources is null || locales is null ||
            string.IsNullOrWhiteSpace(voiceLocale) || clipId.Contains('/'))
        {
            return !clipId.Contains('/') && !string.IsNullOrWhiteSpace(voiceLocale)
                ? $"{voiceLocale}/{clipId}"
                : clipId;
        }

        return localizedResources.ResolvePath(
            clipId,
            voiceLocale,
            locales.GetVoiceFallbackChain(voiceLocale),
            candidate => voiceLoader.Exists(candidate)).ResolvedPath ?? $"{voiceLocale}/{clipId}";
    }

    public static string RequireName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private void HandleVoiceLocaleChanged(LocaleChangedEvent change) =>
        VoiceLocale = change.CurrentLocale;
}
