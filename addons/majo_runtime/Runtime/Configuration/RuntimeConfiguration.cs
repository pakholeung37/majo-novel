using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class RuntimeConfiguration : Resource
{
    [Export(PropertyHint.Range, "0.1,60,0.1,or_greater")]
    public double LifecycleTimeoutSeconds { get; set; } = RuntimeDefaults.LifecycleTimeoutSeconds;
}
