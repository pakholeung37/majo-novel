#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class DialoguePresentationConfiguration : Resource
{
    public const string DefaultScenePath =
        "res://addons/majo_runtime/Runtime/UI/Dialogue/DefaultVnPresentation.tscn";

    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public PackedScene? PresenterScene { get; set; }

    [Export]
    public Theme? Theme { get; set; }

    [Export]
    public Font? Font { get; set; }

    [Export(PropertyHint.Range, "1,240,1,or_greater")]
    public int LettersPerSecond { get; set; } = 32;

    [Export]
    public bool UseFadeEffect { get; set; }

    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public double FadeUpDurationSeconds { get; set; } = 0.1;

    [Export(PropertyHint.Range, "0,5,0.01,or_greater")]
    public double FadeDownDurationSeconds { get; set; } = 0.1;
}
