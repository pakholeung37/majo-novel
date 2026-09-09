using System;
using Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ResourceConfiguration : Resource, IValidatableConfiguration
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

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(RootPath) ||
            !RootPath.StartsWith("res://", StringComparison.Ordinal))
        {
            context.AddIssue(
                nameof(RootPath),
                "Root path must be a Godot project path starting with 'res://'.");
        }

        ValidateResourceKeyPrefix(CharactersPath, nameof(CharactersPath), context);
        ValidateResourceKeyPrefix(BackgroundsPath, nameof(BackgroundsPath), context);
        ValidateResourceKeyPrefix(ScriptsPath, nameof(ScriptsPath), context);

        if (MemoryBudgetMiB < 0)
        {
            context.AddIssue(
                nameof(MemoryBudgetMiB),
                "Memory budget cannot be negative.");
        }

        if (PreloadVoiceLines < 0 || PreloadVoiceLines > 32 ||
            PreloadBudgetMiB < 0 || PreloadMaxResources < 0 ||
            PreloadTextureEstimateMiB <= 0 || PreloadAudioEstimateKiB <= 0)
        {
            context.AddIssue(
                "Preloading",
                "Preload limits must be non-negative, voice look-ahead at most 32 lines, and estimates positive.");
        }

        if (DefaultResourceEstimateKiB < 0)
        {
            context.AddIssue(
                nameof(DefaultResourceEstimateKiB),
                "Default resource estimate cannot be negative.");
        }
    }

    private static void ValidateResourceKeyPrefix(
        string value,
        string property,
        ConfigurationValidationContext context)
    {
        try
        {
            _ = new ResourceKey(value);
        }
        catch (ArgumentException exception)
        {
            context.AddIssue(property, exception.Message);
        }
    }
}
