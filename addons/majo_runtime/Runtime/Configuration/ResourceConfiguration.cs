using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ResourceConfiguration : Resource
{
    public const string DefaultRootPath = "res://assets";
    public const string DefaultCharactersPath = "characters";
    public const string DefaultBackgroundsPath = "backgrounds";
    public const string DefaultObjectsPath = "objects";
    public const string DefaultScriptsPath = "scripts";

    [Export(PropertyHint.Dir)]
    public string RootPath { get; set; } = DefaultRootPath;

    [Export]
    public string CharactersPath { get; set; } = DefaultCharactersPath;

    [Export]
    public string BackgroundsPath { get; set; } = DefaultBackgroundsPath;

    [Export]
    public string ObjectsPath { get; set; } = DefaultObjectsPath;

    [Export]
    public string ScriptsPath { get; set; } = DefaultScriptsPath;

    [Export]
    public bool FailOnMissingAssets { get; set; }

    /// <summary>Cache budget in MiB. Zero disables the byte cap and evicts leased resources when their last owner releases them.</summary>
    [Export(PropertyHint.Range, "0,16384,1,or_greater")]
    public int MemoryBudgetMiB { get; set; } = 0;

    [Export] public bool EnableYarnPreloading { get; set; } = true;
    [Export(PropertyHint.Range, "0,32,1")] public int PreloadVoiceLines { get; set; } = 4;
    /// <summary>Look-ahead budget in MiB. Zero disables the byte cap; voice and resource counts still limit the window.</summary>
    [Export(PropertyHint.Range, "0,4096,1")] public int PreloadBudgetMiB { get; set; } = 0;
    [Export(PropertyHint.Range, "0,128,1")] public int PreloadMaxResources { get; set; } = 12;
    [Export(PropertyHint.Range, "1,1024,1")] public int PreloadTextureEstimateMiB { get; set; } = 32;
    [Export(PropertyHint.Range, "1,65536,1")] public int PreloadAudioEstimateKiB { get; set; } = 2048;

    [Export(PropertyHint.Range, "0,1024,1,or_greater")]
    public int DefaultResourceEstimateKiB { get; set; } = 256;
}
