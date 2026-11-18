#nullable enable

using System;
using Godot;
using Godot.Collections;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class LocalizationConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public string DefaultLocale { get; set; } = "en";

    [Export]
    public Array<string> SupportedLocales { get; set; } = new() { "en", "zh-CN" };

    [Export]
    public string? FallbackLocale { get; set; }

    [Export]
    public string LocalesPath { get; set; } = "res://localization";

    [Export]
    public string FontsPath { get; set; } = "res://assets/fonts";

    [Export(PropertyHint.Range, "0.5,2.0,0.05,or_greater")]
    public double FontScale { get; set; } = 1.0;

    [Export(PropertyHint.Range, "0,32,1,or_greater")]
    public int LineSpacing { get; set; }

    [Export]
    public bool FailOnMissingTranslation { get; set; }

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(DefaultLocale))
        {
            context.AddIssue(
                nameof(DefaultLocale),
                "Default locale is required.");
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
    }
}
