#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class GlobalStateConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public bool StrictRecovery { get; set; }

    [Export(PropertyHint.Range, "0,5,0.05,or_greater")]
    public double FlushDebounceSeconds { get; set; } = RuntimeDefaults.GlobalStateFlushDebounceSeconds;

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!double.IsFinite(FlushDebounceSeconds) || FlushDebounceSeconds < 0)
        {
            context.AddIssue(
                nameof(FlushDebounceSeconds),
                "Flush debounce must be finite and non-negative.");
        }
    }
}
