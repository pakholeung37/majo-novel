using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ResourceConfiguration : Resource
{
    public const string DefaultRootPath = "res://assets";
    public const string DefaultCharactersPath = "characters";
    public const string DefaultBackgroundsPath = "backgrounds";
    public const string DefaultScriptsPath = "scripts";

    [Export(PropertyHint.Dir)]
    public string RootPath { get; set; } = DefaultRootPath;

    [Export]
    public string CharactersPath { get; set; } = DefaultCharactersPath;

    [Export]
    public string BackgroundsPath { get; set; } = DefaultBackgroundsPath;

    [Export]
    public string ScriptsPath { get; set; } = DefaultScriptsPath;

    [Export]
    public bool FailOnMissingAssets { get; set; }

    [Export(PropertyHint.Range, "0,16384,1,or_greater")]
    public int MemoryBudgetMiB { get; set; } = 512;

    [Export(PropertyHint.Range, "0,1024,1,or_greater")]
    public int DefaultResourceEstimateKiB { get; set; } = 256;
}
