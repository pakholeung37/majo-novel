#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class GlobalStateConfiguration : Resource
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public bool StrictRecovery { get; set; }

    [Export(PropertyHint.Range, "0,5,0.05,or_greater")]
    public double FlushDebounceSeconds { get; set; } = RuntimeDefaults.GlobalStateFlushDebounceSeconds;
}
