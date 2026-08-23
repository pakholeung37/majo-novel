#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class AudioConfiguration : Resource
{
    public const string DefaultBgmPath = "audio/bgm";
    public const string DefaultSfxPath = "audio/sfx";
    public const string DefaultVoicePath = "audio/voice";

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float MasterVolume { get; set; } = 1;

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float BgmVolume { get; set; } = 1;

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SfxVolume { get; set; } = 1;

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float VoiceVolume { get; set; } = 1;

    [Export]
    public bool MasterMuted { get; set; }

    [Export]
    public bool BgmMuted { get; set; }

    [Export]
    public bool SfxMuted { get; set; }

    [Export]
    public bool VoiceMuted { get; set; }

    [Export(PropertyHint.Range, "0,10,0.01,or_greater")]
    public double DefaultFadeDurationSeconds { get; set; } = 0.35;

    [Export]
    public string VoiceLocale { get; set; } = "en";

    [Export]
    public string BgmPath { get; set; } = DefaultBgmPath;

    [Export]
    public string SfxPath { get; set; } = DefaultSfxPath;

    [Export]
    public string VoicePath { get; set; } = DefaultVoicePath;
}
