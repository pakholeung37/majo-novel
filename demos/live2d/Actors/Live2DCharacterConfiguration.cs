#nullable enable

using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;

namespace Majo.Demos.Live2D.Actors;

[GlobalClass]
public partial class Live2DCharacterConfiguration : Resource
{
    [Export(PropertyHint.File, "*.model3.json")]
    public string ModelPath { get; set; } = string.Empty;

    [Export]
    public string DefaultAppearance { get; set; } = "neutral";

    [Export]
    public CharacterPositionPreset InitialPosition { get; set; } =
        CharacterPositionPreset.Center;

    [Export(PropertyHint.Range, "1,2160,1,or_greater")]
    public float DisplayHeight { get; set; } = 980;

    [Export]
    public Vector2 VisualOffset { get; set; } = new(0, -490);

    [Export]
    public Godot.Collections.Dictionary<string, string> Expressions { get; set; } = new();

    /// <summary>Maps an authored motion name to a Cubism "group:index" pair.</summary>
    [Export]
    public Godot.Collections.Dictionary<string, string> Motions { get; set; } = new();
}

public sealed record Live2DCharacterDefinitionData(
    string ModelPath,
    string DefaultAppearance,
    CharacterPositionPreset InitialPosition,
    float DisplayHeight,
    Vector2 VisualOffset,
    IReadOnlyDictionary<string, string> Expressions,
    IReadOnlyDictionary<string, Live2DMotionDefinition> Motions);

public sealed record Live2DMotionDefinition(string Group, int Index);
