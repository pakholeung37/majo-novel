using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class OperationConfiguration : Resource
{
    [Export(PropertyHint.Range, "0.1,60,0.1,or_greater")]
    public double DefaultAnimationDurationSeconds { get; set; } = 0.35;
}
