using System;
using Godot;

namespace Majo.Runtime.Actors.Godot;

public static class StageCoordinates
{
    public static Vector2 ToPixels(Vector2 percent, Vector2I resolution)
    {
        if (!float.IsFinite(percent.X) || !float.IsFinite(percent.Y))
            throw new ArgumentOutOfRangeException(nameof(percent), "Stage coordinates must be finite.");
        if (resolution.X <= 0 || resolution.Y <= 0)
            throw new ActorException("Stage coordinates require a positive reference resolution.");
        return new Vector2(resolution.X * percent.X / 100f, resolution.Y * (1f - percent.Y / 100f));
    }

    public static Vector2 ToPercent(Vector2 pixels, Vector2I resolution)
    {
        if (resolution.X <= 0 || resolution.Y <= 0)
            throw new ActorException("Stage coordinates require a positive reference resolution.");
        return new Vector2(pixels.X / resolution.X * 100f, (1f - pixels.Y / resolution.Y) * 100f);
    }
}
