#nullable enable

using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class SaveConfiguration : Resource
{
    [Export]
    public bool Enabled { get; set; }

    [Export(PropertyHint.Range, "1,100,1")]
    public int ManualSlotCapacity { get; set; } = 20;

    [Export(PropertyHint.Range, "1,20,1")]
    public int QuickSlotCapacity { get; set; } = 3;

    [Export(PropertyHint.Range, "1,20,1")]
    public int AutoSlotCapacity { get; set; } = 5;

    [Export]
    public bool CaptureThumbnails { get; set; } = true;

    [Export(PropertyHint.Range, "64,1920,1")]
    public int ThumbnailWidth { get; set; } = 320;

    [Export(PropertyHint.Range, "36,1080,1")]
    public int ThumbnailHeight { get; set; } = 180;

    [Export]
    public bool ShowDefaultUi { get; set; } = true;

    [Export]
    public PackedScene? DefaultUiScene { get; set; }

    [Export]
    public string[] AutoSaveNodeIds { get; set; } = [];

    [Export]
    public string[] AutoSaveLineIds { get; set; } = [];

    [Export]
    public string[] AutoSaveMetadata { get; set; } = [];
}
