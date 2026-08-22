using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ResourceConfiguration : Resource
{
    public const string DefaultRootPath = "res://assets";
    public const string DefaultCharactersPath = "characters";
    public const string DefaultBackgroundsPath = "backgrounds";
    public const string DefaultDialoguePath = "dialogue";

    [Export(PropertyHint.Dir)]
    public string RootPath { get; set; } = DefaultRootPath;

    [Export]
    public string CharactersPath { get; set; } = DefaultCharactersPath;

    [Export]
    public string BackgroundsPath { get; set; } = DefaultBackgroundsPath;

    [Export]
    public string DialoguePath { get; set; } = DefaultDialoguePath;
}
