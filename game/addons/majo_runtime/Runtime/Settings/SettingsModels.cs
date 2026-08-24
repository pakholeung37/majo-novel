#nullable enable

using System;

namespace Majo.Runtime.Settings;

public enum SkipDefaultMode
{
    ReadOnly,
    All,
}

public sealed record RuntimeSettings(
    float MasterVolume = 1,
    float BgmVolume = 1,
    float SfxVolume = 1,
    float VoiceVolume = 1,
    bool MasterMuted = false,
    bool BgmMuted = false,
    bool SfxMuted = false,
    bool VoiceMuted = false,
    int LettersPerSecond = 32,
    double AutoExtraDelaySeconds = 1,
    bool AutoWaitForVoice = true,
    SkipDefaultMode SkipDefaultMode = SkipDefaultMode.ReadOnly,
    double SkipDelaySeconds = 0.05,
    bool ShowSpeakerName = true,
    bool ShowTextBox = true,
    string Language = "en")
{
    public RuntimeSettings Validate()
    {
        ValidateVolume(MasterVolume, nameof(MasterVolume));
        ValidateVolume(BgmVolume, nameof(BgmVolume));
        ValidateVolume(SfxVolume, nameof(SfxVolume));
        ValidateVolume(VoiceVolume, nameof(VoiceVolume));
        if (LettersPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LettersPerSecond));
        }
        if (!double.IsFinite(AutoExtraDelaySeconds) || AutoExtraDelaySeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AutoExtraDelaySeconds));
        }
        if (!double.IsFinite(SkipDelaySeconds) || SkipDelaySeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SkipDelaySeconds));
        }
        if (!Enum.IsDefined(SkipDefaultMode))
        {
            throw new ArgumentOutOfRangeException(nameof(SkipDefaultMode));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(Language);
        return this;
    }

    private static void ValidateVolume(float value, string name)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(name, "Volume must be between zero and one.");
        }
    }
}

public enum ScopedStateRecoveryKind
{
    None,
    Missing,
    Loaded,
    RecoveredFromCorruption,
    StrictFailure,
}

public sealed record ScopedStateRecoveryStatus(
    ScopedStateRecoveryKind Kind,
    string? Message = null,
    string? QuarantineKey = null,
    string? BackupKey = null);

public sealed record SettingsChanged(RuntimeSettings Previous, RuntimeSettings Current);

public interface ISettingsProjection
{
    void Apply(RuntimeSettings settings);
}
