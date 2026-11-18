#nullable enable

using System;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Actors;

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

