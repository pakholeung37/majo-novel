#nullable enable

using System;

namespace Majo.Runtime.Timing;

public enum AnimationTimeMode
{
    Scaled,
    Unscaled,
}

public readonly record struct AnimationTiming
{
    public AnimationTiming(
        double durationSeconds,
        EasingId easing = default,
        AnimationTimeMode timeMode = AnimationTimeMode.Scaled)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds), "Duration must be finite and non-negative.");
        }

        DurationSeconds = durationSeconds;
        Easing = easing.IsEmpty ? EasingId.Linear : easing;
        TimeMode = timeMode;
    }

    public double DurationSeconds { get; }
    public EasingId Easing { get; }
    public AnimationTimeMode TimeMode { get; }

    public static AnimationTiming Immediate { get; } = new(0, EasingId.Linear);
}
