#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class StateConfiguration : Resource
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public string StorageNamespace { get; set; } = "majo/state";

    [Export]
    public string GameBuildId { get; set; } = "development";

    [Export]
    public DialogueTravelConfiguration DialogueTravel { get; set; } = new();
}
