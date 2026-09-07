#nullable enable

using System;

namespace Majo.Runtime.Audio;

internal sealed class AudioMixer
{
    private float masterVolume;
    private float bgmVolume;
    private float sfxVolume;
    private float voiceVolume;
    private bool masterMuted;
    private bool bgmMuted;
    private bool sfxMuted;
    private bool voiceMuted;

    public AudioMixer(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        masterVolume = ValidateVolume(settings.MasterVolume);
        bgmVolume = ValidateVolume(settings.BgmVolume);
        sfxVolume = ValidateVolume(settings.SfxVolume);
        voiceVolume = ValidateVolume(settings.VoiceVolume);
        masterMuted = settings.MasterMuted;
        bgmMuted = settings.BgmMuted;
        sfxMuted = settings.SfxMuted;
        voiceMuted = settings.VoiceMuted;
    }

    public event Action<AudioCategory?>? VolumeChanged;

    public float MasterVolume
    {
        get => masterVolume;
        set
        {
            masterVolume = ValidateVolume(value);
            VolumeChanged?.Invoke(null);
        }
    }

    public float BgmVolume
    {
        get => bgmVolume;
        set
        {
            bgmVolume = ValidateVolume(value);
            VolumeChanged?.Invoke(AudioCategory.Bgm);
        }
    }

    public float SfxVolume
    {
        get => sfxVolume;
        set
        {
            sfxVolume = ValidateVolume(value);
            VolumeChanged?.Invoke(AudioCategory.Sfx);
        }
    }

    public float VoiceVolume
    {
        get => voiceVolume;
        set
        {
            voiceVolume = ValidateVolume(value);
            VolumeChanged?.Invoke(AudioCategory.Voice);
        }
    }

    public bool MasterMuted
    {
        get => masterMuted;
        set
        {
            masterMuted = value;
            VolumeChanged?.Invoke(null);
        }
    }

    public bool BgmMuted
    {
        get => bgmMuted;
        set
        {
            bgmMuted = value;
            VolumeChanged?.Invoke(AudioCategory.Bgm);
        }
    }

    public bool SfxMuted
    {
        get => sfxMuted;
        set
        {
            sfxMuted = value;
            VolumeChanged?.Invoke(AudioCategory.Sfx);
        }
    }

    public bool VoiceMuted
    {
        get => voiceMuted;
        set
        {
            voiceMuted = value;
            VolumeChanged?.Invoke(AudioCategory.Voice);
        }
    }

    public float CalculateEffectiveVolume(AudioCategory category, float baseVolume)
    {
        if (masterMuted || IsMuted(category))
        {
            return 0;
        }

        return masterVolume * CategoryVolume(category) * baseVolume;
    }

    public bool IsMuted(AudioCategory category) => category switch
    {
        AudioCategory.Bgm => bgmMuted,
        AudioCategory.Sfx => sfxMuted,
        AudioCategory.Voice => voiceMuted,
        _ => false,
    };

    public float CategoryVolume(AudioCategory category) => category switch
    {
        AudioCategory.Bgm => bgmVolume,
        AudioCategory.Sfx => sfxVolume,
        AudioCategory.Voice => voiceVolume,
        _ => 1,
    };

    public static float ValidateVolume(float value)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), "Volume must be finite and between zero and one.");
        }

        return value;
    }
}
