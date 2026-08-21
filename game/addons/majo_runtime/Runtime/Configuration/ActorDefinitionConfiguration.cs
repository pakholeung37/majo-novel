using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorDefinitionConfiguration : Resource
{
    [Export]
    public string Id { get; set; } = string.Empty;

    [Export(PropertyHint.File)]
    public string AssetPath { get; set; } = string.Empty;
}
