using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class InputConfiguration : Resource
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public bool InstallDefaultBindings { get; set; } = true;
}
