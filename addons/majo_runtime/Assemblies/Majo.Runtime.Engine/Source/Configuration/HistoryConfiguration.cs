#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class HistoryConfiguration : Resource, IValidatableConfiguration
{
    [Export(PropertyHint.Range, "1,1000,1,or_greater")]
    public int Capacity { get; set; } = 100;

    [Export]
    public bool ShowDefaultUi { get; set; } = true;

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Capacity <= 0)
        {
            context.AddIssue(
                nameof(Capacity),
                "Capacity must be greater than zero.");
        }
    }
}
