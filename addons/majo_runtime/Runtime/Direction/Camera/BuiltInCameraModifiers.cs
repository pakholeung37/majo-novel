#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Direction.Camera;

public sealed class ShakeCameraModifier : ICameraModifier
{
    public string EffectName => "shake";

    public async Task ExecuteAsync(
        ViewId viewId,
        IReadOnlyDictionary<string, string> parameters,
        double durationSeconds,
        Action<CameraPoseDelta> applyDelta,
        IFrameClock frameClock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyDelta);
        ArgumentNullException.ThrowIfNull(frameClock);
        if (durationSeconds <= 0) return;

        var strength = ReadFloat(parameters, "strength", 8.0f);
        var axis = ReadAxis(parameters, "axis", CameraShakeAxis.All);
        var frequency = ReadInt(parameters, "frequency", 20);

        var samples = Math.Max(1, (int)Math.Ceiling(durationSeconds * frequency));
        var sampleDuration = durationSeconds / samples;
        var random = Random.Shared;
        var initialSign = random.Next(2) == 0 ? 1f : -1f;
        var initialAngle = (float)(random.NextDouble() * Math.PI * 2);

        try
        {
            for (var i = 0; i < samples; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = (float)i / samples;
                var decay = (1f - progress) * (1f - progress);
                var sign = (i % 2 == 0 ? 1f : -1f) * initialSign;
                var noise = 0.7f + 0.3f * (float)random.NextDouble();
                var amount = sign * noise * strength * decay;

                var offset = axis switch
                {
                    CameraShakeAxis.X => new Vector2(amount, 0),
                    CameraShakeAxis.Y => new Vector2(0, amount),
                    CameraShakeAxis.Diagonal => new Vector2(amount, amount),
                    _ => new Vector2(
                        MathF.Cos(initialAngle + i * MathF.PI) * noise * strength * decay,
                        MathF.Sin(initialAngle + i * MathF.PI) * noise * strength * decay),
                };

                applyDelta(new CameraPoseDelta(offset, 1.0f, 0.0f));
                await frameClock.DelayAsync(TimeSpan.FromSeconds(sampleDuration), cancellationToken);
            }
        }
        finally
        {
            applyDelta(CameraPoseDelta.Identity);
        }
    }

    private static float ReadFloat(IReadOnlyDictionary<string, string> p, string key, float fallback) =>
        p.TryGetValue(key, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, string> p, string key, int fallback) =>
        p.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static CameraShakeAxis ReadAxis(IReadOnlyDictionary<string, string> p, string key, CameraShakeAxis fallback) =>
        p.TryGetValue(key, out var s) && Enum.TryParse<CameraShakeAxis>(s, true, out var v) ? v : fallback;
}

public sealed class PunchCameraModifier : ICameraModifier
{
    public string EffectName => "punch";

    public async Task ExecuteAsync(
        ViewId viewId,
        IReadOnlyDictionary<string, string> parameters,
        double durationSeconds,
        Action<CameraPoseDelta> applyDelta,
        IFrameClock frameClock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyDelta);
        ArgumentNullException.ThrowIfNull(frameClock);
        if (durationSeconds <= 0) return;

        var strength = ReadFloat(parameters, "strength", 20.0f);
        var axis = ReadAxis(parameters, "axis", CameraShakeAxis.Y);
        var steps = Math.Max(1, (int)Math.Ceiling(durationSeconds * 60));
        var stepDuration = durationSeconds / steps;

        try
        {
            for (var i = 0; i <= steps; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = (float)i / steps;
                // Elastic punch decay curve
                var envelope = (1.0f - progress) * (1.0f - progress);
                var oscillation = MathF.Cos(progress * MathF.PI * 6);
                var displacement = strength * envelope * oscillation;

                var offset = axis switch
                {
                    CameraShakeAxis.X => new Vector2(displacement, 0),
                    CameraShakeAxis.Diagonal => new Vector2(displacement, displacement),
                    _ => new Vector2(0, displacement),
                };

                applyDelta(new CameraPoseDelta(offset, 1.0f, 0.0f));
                if (i < steps)
                {
                    await frameClock.DelayAsync(TimeSpan.FromSeconds(stepDuration), cancellationToken);
                }
            }
        }
        finally
        {
            applyDelta(CameraPoseDelta.Identity);
        }
    }

    private static float ReadFloat(IReadOnlyDictionary<string, string> p, string key, float fallback) =>
        p.TryGetValue(key, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static CameraShakeAxis ReadAxis(IReadOnlyDictionary<string, string> p, string key, CameraShakeAxis fallback) =>
        p.TryGetValue(key, out var s) && Enum.TryParse<CameraShakeAxis>(s, true, out var v) ? v : fallback;
}

public sealed class ZoomPulseCameraModifier : ICameraModifier
{
    private readonly string effectName;

    public ZoomPulseCameraModifier(string effectName = "zoom_pulse")
    {
        this.effectName = effectName;
    }

    public string EffectName => effectName;

    public async Task ExecuteAsync(
        ViewId viewId,
        IReadOnlyDictionary<string, string> parameters,
        double durationSeconds,
        Action<CameraPoseDelta> applyDelta,
        IFrameClock frameClock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyDelta);
        ArgumentNullException.ThrowIfNull(frameClock);
        if (durationSeconds <= 0) return;

        var targetZoom = ReadFloat(parameters, "zoom", 1.15f);
        var steps = Math.Max(1, (int)Math.Ceiling(durationSeconds * 60));
        var stepDuration = durationSeconds / steps;

        try
        {
            for (var i = 0; i <= steps; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = (float)i / steps;
                // Smooth sine wave pulse: 1.0 -> targetZoom -> 1.0
                var multiplier = 1.0f + (targetZoom - 1.0f) * MathF.Sin(progress * MathF.PI);

                applyDelta(new CameraPoseDelta(Vector2.Zero, multiplier, 0.0f));
                if (i < steps)
                {
                    await frameClock.DelayAsync(TimeSpan.FromSeconds(stepDuration), cancellationToken);
                }
            }
        }
        finally
        {
            applyDelta(CameraPoseDelta.Identity);
        }
    }

    private static float ReadFloat(IReadOnlyDictionary<string, string> p, string key, float fallback) =>
        p.TryGetValue(key, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
