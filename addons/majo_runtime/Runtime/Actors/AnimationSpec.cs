#nullable enable

using System;
using Majo.Runtime.Operations;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Actors;

public readonly record struct AnimationSpec
{
    public AnimationSpec(
        AnimationTiming timing,
        AnimationConflictPolicy conflictPolicy = AnimationConflictPolicy.CompletePrevious)
    {
        Timing = timing;
        ConflictPolicy = conflictPolicy;
    }

    public AnimationSpec(
        double durationSeconds,
        EasingId easing = default,
        AnimationTimeMode timeMode = AnimationTimeMode.Scaled,
        AnimationConflictPolicy conflictPolicy = AnimationConflictPolicy.CompletePrevious)
        : this(new AnimationTiming(durationSeconds, easing, timeMode), conflictPolicy)
    {
    }

    public AnimationTiming Timing { get; }

    public AnimationConflictPolicy ConflictPolicy { get; }

    public double DurationSeconds => Timing.DurationSeconds;

    public EasingId Easing => Timing.Easing;

    public AnimationTimeMode TimeMode => Timing.TimeMode;
}
