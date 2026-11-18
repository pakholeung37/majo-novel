#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Actors;

public readonly record struct ActorVisualDelta(Vector2 Offset, float RotationDegrees,
    float ScaleMultiplier, DirectionColor ColorMultiplier, float OpacityMultiplier)
{
    public static ActorVisualDelta Identity => new(Vector2.Zero, 0, 1, DirectionColor.White, 1);
    public ActorVisualDelta Combine(ActorVisualDelta other) => new(Offset + other.Offset,
        RotationDegrees + other.RotationDegrees, ScaleMultiplier * other.ScaleMultiplier,
        new DirectionColor(ColorMultiplier.R * other.ColorMultiplier.R, ColorMultiplier.G * other.ColorMultiplier.G,
            ColorMultiplier.B * other.ColorMultiplier.B, ColorMultiplier.A * other.ColorMultiplier.A),
        OpacityMultiplier * other.OpacityMultiplier);
}

public interface IActorModifierTarget
{
    void ApplyDelta(ActorVisualDelta delta);
}
