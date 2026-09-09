#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class DialoguePresentationConfiguration : Resource, IValidatableConfiguration
{
    public const string DefaultScenePath =
        "res://addons/majo_runtime/Runtime/UI/Dialogue/DefaultVnPresentation.tscn";

    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public PackedScene? PresenterScene { get; set; }

    [Export]
    public Theme? Theme { get; set; }

    [Export]
    public Font? Font { get; set; }

    [Export(PropertyHint.Range, "1,240,1,or_greater")]
    public int LettersPerSecond { get; set; } = 32;

    [Export]
    public bool UseFadeEffect { get; set; }

    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public double FadeUpDurationSeconds { get; set; } = RuntimeDefaults.PresentationFadeDurationSeconds;

    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public double FadeDownDurationSeconds { get; set; } = RuntimeDefaults.PresentationFadeDurationSeconds;

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (LettersPerSecond <= 0)
        {
            context.AddIssue(
                nameof(LettersPerSecond),
                "Value must be greater than zero.");
        }

        ValidateNonNegativeDuration(FadeUpDurationSeconds, nameof(FadeUpDurationSeconds), context);
        ValidateNonNegativeDuration(FadeDownDurationSeconds, nameof(FadeDownDurationSeconds), context);
    }

    private static void ValidateNonNegativeDuration(
        double value,
        string property,
        ConfigurationValidationContext context)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            context.AddIssue(
                property,
                "Value must be finite and non-negative.");
        }
    }
}
