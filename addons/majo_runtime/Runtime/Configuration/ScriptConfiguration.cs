#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ScriptConfiguration : Resource
{
    /// <summary>
    /// Reject checkpoints captured from any different compiled Yarn program.
    /// Disabled by default so stable line checkpoints survive compatible script edits.
    /// </summary>
    [Export]
    public bool RequireExactProgramFingerprint { get; set; }
}
