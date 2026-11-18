#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class CameraEnvironmentConfiguration : Resource, IValidatableConfiguration
{
    [Export] public Godot.Collections.Array<CameraDistancePresetConfiguration> DistancePresets { get; set; } = [];
    [Export] public Godot.Collections.Array<CameraDollyPresetConfiguration> DollyPresets { get; set; } = [];
    [Export] public Godot.Collections.Array<CameraParallaxPresetConfiguration> ParallaxPresets { get; set; } = [];
    [Export] public Godot.Collections.Array<CameraIdlePresetConfiguration> IdlePresets { get; set; } = [];

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateNames(DistancePresets.Select(value => value?.Name), nameof(DistancePresets), context);
        ValidateNames(DollyPresets.Select(value => value?.Name), nameof(DollyPresets), context);
        ValidateNames(ParallaxPresets.Select(value => value?.Name), nameof(ParallaxPresets), context);
        ValidateNames(IdlePresets.Select(value => value?.Name), nameof(IdlePresets), context);
    }

    private static void ValidateNames(IEnumerable<string?> values, string path, ConfigurationValidationContext context)
    {
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var name in values)
        {
            if (string.IsNullOrWhiteSpace(name))
                context.AddIssue($"{path}[{i}]", "Camera preset and its name are required.");
            else if (!names.Add(name.Trim()))
                context.AddIssue($"{path}[{i}]", $"Duplicate camera preset '{name}'.");
            i++;
        }
    }
}

[GlobalClass]
public partial class CameraDistancePresetConfiguration : Resource
{
    [Export] public string Name { get; set; } = string.Empty;
    [Export(PropertyHint.Range, "1,100,0.1")] public float Distance { get; set; } = 1;
}

[GlobalClass]
public partial class CameraDollyPresetConfiguration : Resource
{
    [Export] public string Name { get; set; } = string.Empty;
    [Export(PropertyHint.Range, "-2,0.85,0.01")] public float Dolly { get; set; }
}

[GlobalClass]
public partial class CameraParallaxPresetConfiguration : Resource
{
    [Export] public string Name { get; set; } = string.Empty;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Strength { get; set; }
    [Export] public Vector2 AnchorPercent { get; set; } = new(50, 50);
    [Export] public Vector2 LimitPercent { get; set; }
}

[GlobalClass]
public partial class CameraIdlePresetConfiguration : Resource
{
    [Export] public string Name { get; set; } = string.Empty;
    [Export] public Vector2 AmplitudePercent { get; set; }
    [Export(PropertyHint.Range, "0.01,120,0.01,or_greater")] public double PeriodSeconds { get; set; } = 12;
}
