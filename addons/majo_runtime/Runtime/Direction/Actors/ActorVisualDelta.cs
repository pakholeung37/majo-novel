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

public enum ActorFxSlot { Instance, Backdrop }
public enum ActorShakeAxis { All, X, Y, Diagonal }

public readonly record struct ActorFxTarget(StageId StageId, ActorFxSlot Slot, ActorId? ActorId = null);

public sealed record ActorFxCue(ActorFxTarget Target, DirectionActorKind Kind, string EffectName,
    double DurationSeconds = .4, float Strength = 8, ActorShakeAxis Axis = ActorShakeAxis.All,
    int Frequency = 20, DirectionSourceLocation? SourceLocation = null) : ITransientDirectionCue
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EffectName)) throw new DirectionException("Actor FX requires effect.");
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < 0 || !float.IsFinite(Strength) || Strength < 0 || Frequency <= 0 || !Enum.IsDefined(Axis))
            throw new DirectionException("Actor FX requires non-negative finite duration/strength, a positive integer frequency and a valid axis.");
        if (Target.Slot == ActorFxSlot.Instance && Target.ActorId is null)
            throw new DirectionException("Instance FX requires an actor id.");
    }
}

public interface IActorModifierTarget
{
    void ApplyDelta(ActorVisualDelta delta);
}
