#nullable enable

using System;

namespace Majo.Runtime.Actors;

public enum AnimationTransition
{
    Linear,
    Sine,
    Cubic,
    Quint,
    Back,
}

public enum AnimationEase
{
    In,
    Out,
    InOut,
    OutIn,
}

public enum AnimationTimeMode
{
    Scaled,
    Unscaled,
}

public enum AnimationConflictPolicy
{
    CompletePrevious,
    CancelPrevious,
}

public readonly record struct AnimationSpec
{
    public AnimationSpec(
        double durationSeconds,
        AnimationTransition transition = AnimationTransition.Linear,
        AnimationEase ease = AnimationEase.InOut,
        AnimationTimeMode timeMode = AnimationTimeMode.Scaled,
        AnimationConflictPolicy conflictPolicy = AnimationConflictPolicy.CompletePrevious)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds), "Duration must be finite and non-negative.");
        }

        DurationSeconds = durationSeconds;
        Transition = transition;
        Ease = ease;
        TimeMode = timeMode;
        ConflictPolicy = conflictPolicy;
    }

    public double DurationSeconds { get; }

    public AnimationTransition Transition { get; }

    public AnimationEase Ease { get; }

    public AnimationTimeMode TimeMode { get; }

    public AnimationConflictPolicy ConflictPolicy { get; }
}
