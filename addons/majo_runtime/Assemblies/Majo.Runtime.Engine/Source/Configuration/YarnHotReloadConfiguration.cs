using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class YarnHotReloadConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export(PropertyHint.Range, "50,5000,10,or_greater")]
    public int DebounceMilliseconds { get; set; } = 250;

    [Export(PropertyHint.Range, "250,30000,250,or_greater")]
    public int ConsistencyScanMilliseconds { get; set; } = 2000;

    [Export(PropertyHint.Range, "250,30000,250,or_greater")]
    public int ApplyTimeoutMilliseconds { get; set; } = 3000;

    [Export]
    public bool EnableInExportedDebugBuild { get; set; }

    [Export(PropertyHint.Dir)]
    public string SourceRootOverride { get; set; } = string.Empty;

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (DebounceMilliseconds < 0 ||
            ConsistencyScanMilliseconds <= 0 ||
            ApplyTimeoutMilliseconds <= 0)
        {
            context.AddIssue(string.Empty, "Hot reload timing values must be positive.");
        }
    }
}
