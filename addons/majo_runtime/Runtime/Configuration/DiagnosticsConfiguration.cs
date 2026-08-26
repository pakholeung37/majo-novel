#nullable enable

using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class DiagnosticsConfiguration : Resource
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
    public bool EnableOverlay { get; set; }

    [Export]
    public bool defaultOverlayVisible { get; set; } = false;

    [Export]
    public string OverlayAction { get; set; } = "majo_debug";
}
