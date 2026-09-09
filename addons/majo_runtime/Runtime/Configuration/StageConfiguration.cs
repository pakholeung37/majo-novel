using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class StageConfiguration : Resource, IValidatableConfiguration
{
    [Export(PropertyHint.Range, "1,32,1")]
    public int MaxDirectionStages { get; set; } = 8;

    [Export]
    public bool ReduceMotion { get; set; }

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (MaxDirectionStages <= 0)
        {
            context.AddIssue(
                nameof(MaxDirectionStages),
                "Direction Stage limit must be positive.");
        }
    }
}
