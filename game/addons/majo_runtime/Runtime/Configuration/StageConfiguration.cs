using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class StageConfiguration : Resource
{
    [Export]
    public bool ReduceMotion { get; set; }

    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public double DefaultTransitionDurationSeconds { get; set; } = 0.5;

    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public double DefaultEffectDurationSeconds { get; set; } = 0.35;
}
