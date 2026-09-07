#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Timing;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Direction.Godot;

public enum DirectionEaseCurve
{
    Linear,
    Sine,
    Cubic,
    Back,
}

public enum DirectionEaseMode
{
    In,
    Out,
    InOut,
}

public readonly record struct DirectionEaseParts(
    DirectionEaseCurve Curve,
    DirectionEaseMode Mode);

public static class DirectionEaseExtensions
{
    public static DirectionEaseParts Decompose(this DirectionEase ease)
    {
        if (ease == DirectionEase.Linear)
        {
            return new(DirectionEaseCurve.Linear, DirectionEaseMode.InOut);
        }

        var name = ease.ToString();
        foreach (var (suffix, mode) in new[]
        {
            ("InOut", DirectionEaseMode.InOut),
            ("In", DirectionEaseMode.In),
            ("Out", DirectionEaseMode.Out),
        })
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var curveName = name[..^suffix.Length];
            if (Enum.TryParse<DirectionEaseCurve>(curveName, out var curve))
            {
                return new(curve, mode);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(ease), ease, null);
    }
}

public static class GodotEasingAdapter
{
    private static readonly Dictionary<EasingId, (Tween.TransitionType Trans, Tween.EaseType Ease)> NativeTweenMap =
        new()
        {
            [EasingId.Linear] = (Tween.TransitionType.Linear, Tween.EaseType.InOut),
            [EasingId.SineIn] = (Tween.TransitionType.Sine, Tween.EaseType.In),
            [EasingId.SineOut] = (Tween.TransitionType.Sine, Tween.EaseType.Out),
            [EasingId.SineInOut] = (Tween.TransitionType.Sine, Tween.EaseType.InOut),
            [EasingId.CubicIn] = (Tween.TransitionType.Cubic, Tween.EaseType.In),
            [EasingId.CubicOut] = (Tween.TransitionType.Cubic, Tween.EaseType.Out),
            [EasingId.CubicInOut] = (Tween.TransitionType.Cubic, Tween.EaseType.InOut),
            [EasingId.BackIn] = (Tween.TransitionType.Back, Tween.EaseType.In),
            [EasingId.BackOut] = (Tween.TransitionType.Back, Tween.EaseType.Out),
            [EasingId.BackInOut] = (Tween.TransitionType.Back, Tween.EaseType.InOut),
        };

    public static bool TryGetNativeTween(
        EasingId id,
        out Tween.TransitionType transition,
        out Tween.EaseType ease)
    {
        if (NativeTweenMap.TryGetValue(id, out var pair))
        {
            transition = pair.Trans;
            ease = pair.Ease;
            return true;
        }

        transition = Tween.TransitionType.Linear;
        ease = Tween.EaseType.InOut;
        return false;
    }

    public static void Validate(EasingId easing, IEasingRegistry? registry, ITransitionDriver? driver = null)
    {
        if (TryGetNativeTween(easing, out _, out _)) return;
        if (registry is null || !registry.Contains(easing))
            throw new DirectionException($"Easing '{easing}' is not supported by Godot direction backend.");
        if (driver is not null && !driver.SupportsCustomEasing)
            throw new DirectionException($"Transition '{driver.Id}' does not support custom easing '{easing}'.");
    }

    public static void ApplyProgress(EasingId easing, float progress, IEasingRegistry? registry,
        Action<float> apply, Action<Exception> fail)
    {
        try { apply(SampleProgress(easing, progress, registry)); }
        catch (Exception exception) { fail(exception); }
    }

    public static void Configure(Tween tween, EasingId easing, IEasingRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(tween);

        if (TryGetNativeTween(easing, out var trans, out var ease))
        {
            tween.SetTrans(trans);
            tween.SetEase(ease);
            return;
        }

        if (registry is not null && registry.TryGet(easing, out _))
        {
            // For custom easing, run Linear in Tween and sample using SampleProgress
            tween.SetTrans(Tween.TransitionType.Linear);
            tween.SetEase(Tween.EaseType.InOut);
            return;
        }

        throw new DirectionException($"Easing '{easing}' is not supported by Godot direction backend.");
    }

    public static float SampleProgress(EasingId easing, float linearProgress, IEasingRegistry? registry = null)
    {
        if (TryGetNativeTween(easing, out _, out _))
        {
            return linearProgress;
        }

        if (registry is not null && registry.TryGet(easing, out var definition) && definition is not null)
        {
            var clamped = Math.Clamp((double)linearProgress, 0.0, 1.0);
            var value = (float)definition.Evaluate(clamped);
            if (!float.IsFinite(value))
                throw new DirectionException($"Easing '{easing}' returned a non-finite progress at {clamped}.");
            return value;
        }

        throw new DirectionException($"Easing '{easing}' is not registered.");
    }
}
