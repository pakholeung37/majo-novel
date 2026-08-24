#nullable enable

using System;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Presentation;

namespace Majo.Runtime.Settings;

public sealed class RuntimeSettingsProjection : ISettingsProjection
{
    private readonly IAudioManager audio;
    private readonly IPlaybackModeManager playback;
    private readonly IDialoguePresentationSettings? presentation;

    public RuntimeSettingsProjection(
        IAudioManager audio,
        IPlaybackModeManager playback,
        IDialoguePresentation presentation)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.presentation = presentation as IDialoguePresentationSettings;
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
        if (presentation is not null)
        {
            presentation.LettersPerSecond = settings.LettersPerSecond;
        }
    }
}
