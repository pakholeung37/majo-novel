#nullable enable

using System;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Camera;

public readonly record struct DirectionAffineTransform(
    float M11, float M12, float M21, float M22, float TX, float TY)
{
    public DirectionPoint Transform(DirectionPoint point) => new(
        M11 * point.X + M21 * point.Y + TX,
        M12 * point.X + M22 * point.Y + TY);
}

public sealed record CameraProjection(
    DirectionAffineTransform Subject,
    DirectionAffineTransform Background,
    DirectionPoint SampledCenterPixels,
    float SubjectScale,
    float BackgroundScale);

/// <summary>Pure, history-independent layered camera projection.</summary>
public static class CameraProjectionCalculator
{
    public static CameraProjection Calculate(
        ViewPose pose,
        ViewParallaxState parallax,
        ViewIdleState idle,
        float backdropDistance,
        DirectionPoint referenceSizePixels,
        double sampleTimeSeconds = 0)
    {
        pose.Validate(); parallax.Validate(); idle.Validate(); referenceSizePixels.Validate();
        if (referenceSizePixels.X <= 0 || referenceSizePixels.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(referenceSizePixels));
        if (!float.IsFinite(backdropDistance) || backdropDistance is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(backdropDistance));
        if (!double.IsFinite(sampleTimeSeconds))
            throw new ArgumentOutOfRangeException(nameof(sampleTimeSeconds));

        var origin = new DirectionPoint(referenceSizePixels.X / 2, referenceSizePixels.Y / 2);
        var center = ToPixels(pose.Center, referenceSizePixels);
        if (idle.IsEnabled)
        {
            var angle = idle.PhaseRadians + sampleTimeSeconds * Math.Tau / idle.PeriodSeconds;
            center = new DirectionPoint(
                center.X + referenceSizePixels.X * idle.AmplitudePercent.X / 100 * (float)Math.Sin(angle),
                center.Y + referenceSizePixels.Y * idle.AmplitudePercent.Y / 100 * (float)Math.Sin(angle + Math.PI / 2));
        }

        var subjectScale = 1 / (1 - pose.Dolly);
        var physicalBackgroundScale = backdropDistance / (backdropDistance - pose.Dolly);
        var backgroundScale = Lerp(subjectScale, physicalBackgroundScale, parallax.Strength);
        var anchor = ToPixels(parallax.Anchor, referenceSizePixels);
        var k = parallax.Strength * (1 - 1 / backdropDistance);
        var rawOffset = new DirectionPoint(
            pose.Zoom * backgroundScale * k * (center.X - anchor.X),
            pose.Zoom * backgroundScale * k * (center.Y - anchor.Y));
        var limits = new DirectionPoint(
            referenceSizePixels.X * parallax.LimitPercent.X / 100,
            referenceSizePixels.Y * parallax.LimitPercent.Y / 100);
        var boundedOffset = new DirectionPoint(
            Bound(rawOffset.X, limits.X), Bound(rawOffset.Y, limits.Y));

        return new CameraProjection(
            CreateTransform(origin, center, pose.Zoom * subjectScale, pose.RotationDegrees, default),
            CreateTransform(origin, center, pose.Zoom * backgroundScale, pose.RotationDegrees, boundedOffset),
            center,
            subjectScale,
            backgroundScale);
    }

    private static DirectionAffineTransform CreateTransform(
        DirectionPoint origin, DirectionPoint center, float scale, float rotationDegrees, DirectionPoint extraOffset)
    {
        var radians = rotationDegrees * MathF.PI / 180;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var m11 = cos * scale;
        var m12 = sin * scale;
        var m21 = -sin * scale;
        var m22 = cos * scale;
        var offsetX = cos * extraOffset.X - sin * extraOffset.Y;
        var offsetY = sin * extraOffset.X + cos * extraOffset.Y;
        return new DirectionAffineTransform(
            m11, m12, m21, m22,
            origin.X - m11 * center.X - m21 * center.Y + offsetX,
            origin.Y - m12 * center.X - m22 * center.Y + offsetY);
    }

    private static DirectionPoint ToPixels(DirectionPoint percent, DirectionPoint size) =>
        new(size.X * percent.X / 100, size.Y * (1 - percent.Y / 100));

    private static float Bound(float value, float limit) =>
        limit == 0 ? 0 : limit * MathF.Tanh(value / limit);

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;
}
