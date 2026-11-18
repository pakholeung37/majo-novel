#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Actors;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotActorModifierTarget(Node2D root, Func<Vector2> pivot) : IActorModifierTarget
{
    private ActorVisualDelta current = ActorVisualDelta.Identity;
    public void ApplyDelta(ActorVisualDelta delta)
    {
        current = delta;
        Refresh();
    }
    public void Refresh()
    {
        if (!GodotObject.IsInstanceValid(root)) return;
        var center = pivot();
        var basis = new Transform2D(Mathf.DegToRad(current.RotationDegrees),
            Vector2.One * current.ScaleMultiplier, 0, Vector2.Zero);
        root.Transform = new Transform2D(basis.X, basis.Y, center + current.Offset - basis * center);
        root.Modulate = new Color(current.ColorMultiplier.R, current.ColorMultiplier.G,
            current.ColorMultiplier.B, current.ColorMultiplier.A * current.OpacityMultiplier);
    }
}
