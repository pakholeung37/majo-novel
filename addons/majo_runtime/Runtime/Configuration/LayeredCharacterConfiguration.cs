using Godot;
using Majo.Runtime.Actors;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class LayeredCharacterConfiguration : Resource
{
    [Export(PropertyHint.File, "*.json")]
    public string ManifestPath { get; set; } = string.Empty;

    [Export]
    public CharacterPositionPreset InitialPosition { get; set; } =
        CharacterPositionPreset.Center;
}
