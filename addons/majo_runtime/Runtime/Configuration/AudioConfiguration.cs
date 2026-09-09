#nullable enable

using System;
using Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class AudioConfiguration : Resource, IValidatableConfiguration
{
    public const string DefaultBgmPath = "audio/bgm";
    public const string DefaultSfxPath = "audio/sfx";
    public const string DefaultVoicePath = "audio/voice";

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float MasterVolume { get; set; } = 1;

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float BgmVolume { get; set; } = 1;

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float VoiceVolume { get; set; } = 1;

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SfxVolume { get; set; } = 1;

    [Export]
    public bool MasterMuted { get; set; }

    [Export]
    public bool BgmMuted { get; set; }

    [Export]
    public bool SfxMuted { get; set; }

    [Export]
    public bool VoiceMuted { get; set; }

    [Export]
    public string VoiceLocale { get; set; } = "en";

    [Export]
    public string BgmPath { get; set; } = DefaultBgmPath;

    [Export]
    public string SfxPath { get; set; } = DefaultSfxPath;

    [Export]
    public string VoicePath { get; set; } = DefaultVoicePath;

    [Export]
    public bool FailOnMissingClips { get; set; }

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ValidateVolume(MasterVolume, nameof(MasterVolume), context);
        ValidateVolume(BgmVolume, nameof(BgmVolume), context);
        ValidateVolume(SfxVolume, nameof(SfxVolume), context);
        ValidateVolume(VoiceVolume, nameof(VoiceVolume), context);

        if (string.IsNullOrWhiteSpace(VoiceLocale))
        {
            context.AddIssue(
                nameof(VoiceLocale),
                "Voice locale is required.");
        }

        ValidateAudioPath(BgmPath, nameof(BgmPath), context);
        ValidateAudioPath(SfxPath, nameof(SfxPath), context);
        ValidateAudioPath(VoicePath, nameof(VoicePath), context);
    }

    private static void ValidateVolume(
        float value,
        string property,
        ConfigurationValidationContext context)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            context.AddIssue(
                property,
                "Value must be finite and between zero and one.");
        }
    }

    private static void ValidateAudioPath(
        string value,
        string property,
        ConfigurationValidationContext context)
    {
        try
        {
            _ = new ResourceKey(value);
        }
        catch (ArgumentException exception)
        {
            context.AddIssue(
                property,
                exception.Message);
        }
    }
}
