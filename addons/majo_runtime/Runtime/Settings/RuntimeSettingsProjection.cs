#nullable enable

using System;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;
using Majo.Runtime.Localization;

namespace Majo.Runtime.Settings;

public sealed class RuntimeSettingsProjection : ISettingsProjection
{
    private readonly IAudioManager audio;
    private readonly IPlaybackModeManager playback;
    private readonly IDialoguePresentationSettings? presentation;
    private readonly ILocaleManager? localeManager;
    private readonly IDirectionAccessibility? directionAccessibility;

    public RuntimeSettingsProjection(
        IAudioManager audio,
        IPlaybackModeManager playback,
        IDialoguePresentation presentation,
        ILocaleManager? localeManager = null,
        IDirectionAccessibility? directionAccessibility = null)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.presentation = presentation as IDialoguePresentationSettings;
        this.localeManager = localeManager;
        this.directionAccessibility = directionAccessibility;
    }

    public void Apply(RuntimeSettings settings)
    {
        audio.MasterVolume = settings.MasterVolume;
        audio.BgmVolume = settings.BgmVolume;
        audio.SfxVolume = settings.SfxVolume;
        audio.VoiceVolume = settings.VoiceVolume;
        audio.MasterMuted = settings.MasterMuted;
        audio.BgmMuted = settings.BgmMuted;
        audio.SfxMuted = settings.SfxMuted;
        audio.VoiceMuted = settings.VoiceMuted;
        playback.ApplyAutoSettings(
            TimeSpan.FromSeconds(settings.AutoExtraDelaySeconds),
            settings.AutoWaitForVoice);
        playback.ApplySkipSettings(
            TimeSpan.FromSeconds(settings.SkipDelaySeconds),
            settings.SkipDefaultMode == SkipDefaultMode.ReadOnly
                ? PlaybackMode.SkipReadOnly
                : PlaybackMode.SkipAll);
        if (presentation is not null)
        {
            presentation.LettersPerSecond = settings.LettersPerSecond;
        }

        if (localeManager is not null)
        {
            localeManager.Fonts.SetTypography(settings.FontScale, settings.LineSpacing);
        }

        if (!string.IsNullOrWhiteSpace(settings.VoiceLocale))
        {
            audio.VoiceLocale = settings.VoiceLocale;
        }

        if (directionAccessibility is not null)
        {
            directionAccessibility.ReduceMotion = settings.ReduceMotion;
        }
    }
}
