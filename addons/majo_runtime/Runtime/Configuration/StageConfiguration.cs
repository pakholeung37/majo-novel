using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class StageConfiguration : Resource
{
    [Export(PropertyHint.Range, "1,32,1")]
    public int MaxDirectionStages { get; set; } = 8;

    [Export]
    public bool ReduceMotion { get; set; }

    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public double DefaultTransitionDurationSeconds { get; set; } = RuntimeDefaults.TransitionDurationSeconds;

    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public double DefaultEffectDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
}
