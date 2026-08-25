#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ScriptConfiguration : Resource
{
    [Export]
    public bool RequireExactProgramFingerprint { get; set; } = true;
}
