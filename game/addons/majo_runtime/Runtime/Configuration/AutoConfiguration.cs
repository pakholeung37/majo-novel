#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class AutoConfiguration : Resource
{
    [Export(PropertyHint.Range, "0,30,0.05,or_greater")]
    public double ExtraDelaySeconds { get; set; } = 1;

    [Export]
    public bool WaitForVoice { get; set; } = true;
}
