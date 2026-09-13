using System;
using Godot;

namespace Majo.Demos.BackgroundExtension;

public sealed partial class AnimatedBackdropAccent : Polygon2D
{
    private double phase;
    public override void _Process(double delta)
    {
        phase += delta;
        Color = new Color(1, .3f, .45f, .55f + .4f * (float)Math.Sin(phase * 3));
    }
}
