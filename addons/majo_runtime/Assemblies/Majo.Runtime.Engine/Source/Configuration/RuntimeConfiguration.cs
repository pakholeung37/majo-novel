using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class RuntimeConfiguration : Resource, IValidatableConfiguration
{
    [Export(PropertyHint.Range, "0.1,60,0.1,or_greater")]
    public double LifecycleTimeoutSeconds { get; set; } = RuntimeDefaults.LifecycleTimeoutSeconds;

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!double.IsFinite(LifecycleTimeoutSeconds) || LifecycleTimeoutSeconds <= 0)
        {
            context.AddIssue(
                nameof(LifecycleTimeoutSeconds),
                "Value must be finite and greater than zero.");
        }
    }
}
