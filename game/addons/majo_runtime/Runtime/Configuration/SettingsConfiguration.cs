#nullable enable

using Godot;
using Majo.Runtime.Settings;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class SettingsConfiguration : Resource
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export(PropertyHint.Range, "0,5,0.05,or_greater")]
    public double FlushDebounceSeconds { get; set; } = 0.35;

    [Export]
    public bool ShowDefaultUi { get; set; } = true;

    [Export]
    public PackedScene? DefaultUiScene { get; set; }

    [Export]
    public bool ShowSpeakerName { get; set; } = true;

    [Export]
    public bool ShowTextBox { get; set; } = true;

    [Export]
    public string DialogueLocale { get; set; } = "en";

    [Export]
    public string VoiceLocale { get; set; } = "en";

    [Export(PropertyHint.Range, "0.5,2.0,0.05,or_greater")]
    public double FontScale { get; set; } = 1.0;

    [Export(PropertyHint.Range, "0,32,1,or_greater")]
    public int LineSpacing { get; set; }

    [Export]
    public SkipDefaultMode SkipDefaultMode { get; set; } = SkipDefaultMode.ReadOnly;

    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public double SkipDelaySeconds { get; set; } = 0.05;
}
