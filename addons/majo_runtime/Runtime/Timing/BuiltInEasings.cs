#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Timing;

public static class BuiltInEasings
{
    private const double BackC1 = 1.70158;
    private const double BackC2 = BackC1 * 1.525;

    public static readonly EasingDefinition Linear = new(
        EasingId.Linear,
        t => t,
        new[] { "linear" });

    public static readonly EasingDefinition SineIn = new(
        EasingId.SineIn,
        t => 1.0 - Math.Cos((t * Math.PI) / 2.0),
        new[] { "sinein", "SineIn" });

    public static readonly EasingDefinition SineOut = new(
        EasingId.SineOut,
        t => Math.Sin((t * Math.PI) / 2.0),
        new[] { "sineout", "SineOut" });

    public static readonly EasingDefinition SineInOut = new(
        EasingId.SineInOut,
        t => -(Math.Cos(Math.PI * t) - 1.0) / 2.0,
        new[] { "sineinout", "SineInOut" });

    public static readonly EasingDefinition CubicIn = new(
        EasingId.CubicIn,
        t => t * t * t,
        new[] { "cubicin", "CubicIn" });

    public static readonly EasingDefinition CubicOut = new(
        EasingId.CubicOut,
        t => 1.0 - Math.Pow(1.0 - t, 3.0),
        new[] { "cubicout", "CubicOut" });

    public static readonly EasingDefinition CubicInOut = new(
        EasingId.CubicInOut,
        t => t < 0.5
            ? 4.0 * t * t * t
            : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0,
        new[] { "cubicinout", "CubicInOut" });

    public static readonly EasingDefinition BackIn = new(
        EasingId.BackIn,
        t => (BackC1 + 1.0) * t * t * t - BackC1 * t * t,
        new[] { "backin", "BackIn" });

    public static readonly EasingDefinition BackOut = new(
        EasingId.BackOut,
        t => 1.0 + (BackC1 + 1.0) * Math.Pow(t - 1.0, 3.0) + BackC1 * Math.Pow(t - 1.0, 2.0),
        new[] { "backout", "BackOut" });

    public static readonly EasingDefinition BackInOut = new(
        EasingId.BackInOut,
        t => t < 0.5
            ? (Math.Pow(2.0 * t, 2.0) * ((BackC2 + 1.0) * 2.0 * t - BackC2)) / 2.0
            : (Math.Pow(2.0 * t - 2.0, 2.0) * ((BackC2 + 1.0) * (t * 2.0 - 2.0) + BackC2) + 2.0) / 2.0,
        new[] { "backinout", "BackInOut" });

    public static readonly IReadOnlyList<EasingDefinition> All = new[]
    {
        Linear,
        SineIn,
        SineOut,
        SineInOut,
        CubicIn,
        CubicOut,
        CubicInOut,
        BackIn,
        BackOut,
        BackInOut,
    };

    public static void RegisterDefaults(IEasingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var easing in All)
        {
            registry.Register(easing);
        }
    }

    public static IEasingRegistry CreateDefaultRegistry()
    {
        var registry = new EasingRegistry();
        RegisterDefaults(registry);
        return registry;
    }
}
