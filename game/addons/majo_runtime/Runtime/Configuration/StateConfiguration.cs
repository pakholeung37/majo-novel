#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class StateConfiguration : Resource
{
    [Export]
    public bool Enabled { get; set; } = true;
}
