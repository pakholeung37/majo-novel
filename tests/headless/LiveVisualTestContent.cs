using Godot;

namespace Majo.Runtime.HeadlessTests;

internal sealed partial class LiveVisualTestContent : Polygon2D
{
    public int ProcessCount { get; private set; }
    public override void _Process(double delta) => ProcessCount++;
}
