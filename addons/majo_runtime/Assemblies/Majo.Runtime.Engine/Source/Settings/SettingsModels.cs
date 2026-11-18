#nullable enable

using System;
using Majo.Runtime.Configuration;

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
    double AutoExtraDelaySeconds = RuntimeDefaults.AutoExtraDelaySeconds,
    bool AutoWaitForVoice = true,
    SkipDefaultMode SkipDefaultMode = SkipDefaultMode.ReadOnly,
    double SkipDelaySeconds = RuntimeDefaults.SkipDelaySeconds,
    bool ShowSpeakerName = true,
    bool ShowTextBox = true,
    string DialogueLocale = "en",
    string VoiceLocale = "en",
    double FontScale = 1.0,
    int LineSpacing = 0,
    bool ReduceMotion = false,
    string? Language = null)
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
        if (!double.IsFinite(FontScale) || FontScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FontScale), "Font scale must be greater than zero.");
        }
        if (LineSpacing < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LineSpacing), "Line spacing cannot be negative.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(DialogueLocale);
        ArgumentException.ThrowIfNullOrWhiteSpace(VoiceLocale);
        return string.IsNullOrWhiteSpace(Language)
            ? this
            : this with { DialogueLocale = Language, Language = null };
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
