#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class DialogueTravelConfiguration : Resource
{
    [Export]
    public bool Enabled { get; set; }

    [Export(PropertyHint.Range, "2,2048,1,or_greater")]
    public int Capacity { get; set; } = 128;

    [Export]
    public bool EnableInExportedDebugBuild { get; set; }

    [Export]
    public bool ShowDefaultUi { get; set; } = true;
}
