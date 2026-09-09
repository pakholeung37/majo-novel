#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class AutoConfiguration : Resource, IValidatableConfiguration
{
    [Export(PropertyHint.Range, "0,30,0.05,or_greater")]
    public double ExtraDelaySeconds { get; set; } = RuntimeDefaults.AutoExtraDelaySeconds;

    [Export]
    public bool WaitForVoice { get; set; } = true;

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!double.IsFinite(ExtraDelaySeconds) || ExtraDelaySeconds < 0)
        {
            context.AddIssue(
                nameof(ExtraDelaySeconds),
                "Extra delay must be finite and non-negative.");
        }
    }
}
