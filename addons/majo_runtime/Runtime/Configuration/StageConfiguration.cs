using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class StageConfiguration : Resource
{
    [Export(PropertyHint.Range, "1,32,1")]
    public int MaxDirectionStages { get; set; } = 8;

    [Export]
    public bool ReduceMotion { get; set; }

}
