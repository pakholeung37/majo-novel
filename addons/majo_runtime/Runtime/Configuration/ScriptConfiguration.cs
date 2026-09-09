#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ScriptConfiguration : Resource, IValidatableConfiguration
{
    /// <summary>
    /// Reject checkpoints captured from any different compiled Yarn program.
    /// Disabled by default so stable line checkpoints survive compatible script edits.
    /// </summary>
    [Export]
    public bool RequireExactProgramFingerprint { get; set; }

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}
