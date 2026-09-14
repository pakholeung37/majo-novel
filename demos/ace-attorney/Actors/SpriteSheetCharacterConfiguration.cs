#nullable enable

using Godot;
using Majo.Runtime.Actors;

namespace Majo.Demos.AceAttorney.Actors;

[GlobalClass]
public partial class SpriteSheetCharacterConfiguration : Resource
{
    [Export(PropertyHint.File, "*.json")]
    public string ManifestPath { get; set; } = string.Empty;

    [Export]
    public CharacterPositionPreset InitialPosition { get; set; } = CharacterPositionPreset.Center;
}

public sealed record SpriteSheetDefinitionData(string ManifestPath, CharacterPositionPreset InitialPosition);
