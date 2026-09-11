using Godot;
using Majo.Runtime.Actors;

namespace Majo.Demos.Manosaba.Actors.Layered;

[GlobalClass]
public partial class LayeredCharacterConfiguration : Resource
{
    [Export]
    public string AssetId { get; set; } = string.Empty;

    [Export(PropertyHint.File, "*.json")]
    public string ManifestPath { get; set; } = string.Empty;

    [Export]
    public CharacterPositionPreset InitialPosition { get; set; } =
        CharacterPositionPreset.Center;
}

/// <summary>Compiled resource identity; contains no mutable Godot configuration.</summary>
public sealed record LayeredCharacterDefinitionData(
    string AssetId,
    string ManifestPath,
    CharacterPositionPreset InitialPosition);
