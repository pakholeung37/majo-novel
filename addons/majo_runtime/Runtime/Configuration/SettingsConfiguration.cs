#nullable enable

using System;
using Godot;
using Majo.Runtime.Settings;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class SettingsConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export(PropertyHint.Range, "0,5,0.05,or_greater")]
    public double FlushDebounceSeconds { get; set; } = RuntimeDefaults.SettingsFlushDebounceSeconds;

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
    public double SkipDelaySeconds { get; set; } = RuntimeDefaults.SkipDelaySeconds;

    [Export]
    public bool ReduceMotion { get; set; }

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!double.IsFinite(FlushDebounceSeconds) || FlushDebounceSeconds < 0)
        {
            context.AddIssue(
                nameof(FlushDebounceSeconds),
                "Flush debounce must be finite and non-negative.");
        }

        if (!double.IsFinite(SkipDelaySeconds) || SkipDelaySeconds < 0)
        {
            context.AddIssue(
                nameof(SkipDelaySeconds),
                "Skip delay must be finite and non-negative.");
        }

        if (!double.IsFinite(FontScale) || FontScale <= 0)
        {
            context.AddIssue(
                nameof(FontScale),
                "Font scale must be finite and positive.");
        }

        if (LineSpacing < 0)
        {
            context.AddIssue(
                nameof(LineSpacing),
                "Line spacing cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(DialogueLocale))
        {
            context.AddIssue(
                nameof(DialogueLocale),
                "Dialogue locale identity is required.");
        }

        if (string.IsNullOrWhiteSpace(VoiceLocale))
        {
            context.AddIssue(
                nameof(VoiceLocale),
                "Voice locale identity is required.");
        }
    }
}
