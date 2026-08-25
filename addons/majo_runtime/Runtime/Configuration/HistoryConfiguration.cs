#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class HistoryConfiguration : Resource
{
    [Export(PropertyHint.Range, "1,1000,1,or_greater")]
    public int Capacity { get; set; } = 100;

    [Export]
    public bool ShowDefaultUi { get; set; } = true;
}
