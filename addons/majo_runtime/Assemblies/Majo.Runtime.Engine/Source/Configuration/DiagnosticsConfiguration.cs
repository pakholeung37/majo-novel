#nullable enable

using System;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class DiagnosticsConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public RuntimeLogLevel MinimumLevel { get; set; } = RuntimeLogLevel.Information;

    [Export(PropertyHint.Range, "10,5000,10,or_greater")]
    public int RingBufferCapacity { get; set; } = 500;

    [Export]
    public bool EnableFileLogging { get; set; }

    [Export]
    public string FileLogDirectory { get; set; } = "user://logs";

    [Export(PropertyHint.Range, "1024,104857600,1024,or_greater")]
    public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;

    [Export(PropertyHint.Range, "1,20,1,or_greater")]
    public int MaxFileCount { get; set; } = 3;

    [Export]
    public bool IncludeCorrelation { get; set; }

    [Export]
    public bool EnableOverlay { get; set; }

    [Export]
    public bool defaultOverlayVisible { get; set; } = false;

    [Export]
    public bool ShowFpsOverlay { get; set; } = true;

    [Export]
    public string OverlayAction { get; set; } = "majo_debug";

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RingBufferCapacity <= 0)
        {
            context.AddIssue(
                nameof(RingBufferCapacity),
                "Ring buffer capacity must be greater than zero.");
        }

        if (MaxFileSizeBytes <= 0)
        {
            context.AddIssue(
                nameof(MaxFileSizeBytes),
                "Max file size must be greater than zero.");
        }

        if (MaxFileCount <= 0)
        {
            context.AddIssue(
                nameof(MaxFileCount),
                "Max file count must be greater than zero.");
        }
    }
}

