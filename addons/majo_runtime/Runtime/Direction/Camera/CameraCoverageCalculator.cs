#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Camera;

[Flags]
public enum CameraCoverageEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

public readonly record struct CameraCoverageResult(bool IsCovered, CameraCoverageEdges UncoveredEdges);

/// <summary>Checks whether a projected rectangular visual contains the visible camera polygon.</summary>
public static class CameraCoverageCalculator
{
    public static CameraCoverageResult Evaluate(
        DirectionPoint visualPosition,
        DirectionPoint visualSize,
        DirectionAffineTransform projection,
        DirectionPoint viewSize,
        IReadOnlyList<DirectionPoint>? visiblePolygon = null)
    {
        visualPosition.Validate(); visualSize.Validate(); viewSize.Validate();
        if (visualSize.X <= 0 || visualSize.Y <= 0 || viewSize.X <= 0 || viewSize.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(visualSize));

        var visual = new[]
        {
            projection.Transform(visualPosition),
            projection.Transform(new DirectionPoint(visualPosition.X + visualSize.X, visualPosition.Y)),
            projection.Transform(new DirectionPoint(visualPosition.X + visualSize.X, visualPosition.Y + visualSize.Y)),
            projection.Transform(new DirectionPoint(visualPosition.X, visualPosition.Y + visualSize.Y)),
        };
        var visible = visiblePolygon ?? new[]
        {
            new DirectionPoint(0, 0),
            new DirectionPoint(viewSize.X, 0),
            new DirectionPoint(viewSize.X, viewSize.Y),
            new DirectionPoint(0, viewSize.Y),
        };
        if (visible.Count == 0) return new CameraCoverageResult(true, CameraCoverageEdges.None);

        var uncovered = CameraCoverageEdges.None;
        foreach (var point in visible)
        {
            point.Validate();
            if (ContainsConvex(visual, point)) continue;
            if (point.X <= viewSize.X / 2) uncovered |= CameraCoverageEdges.Left;
            if (point.X >= viewSize.X / 2) uncovered |= CameraCoverageEdges.Right;
            if (point.Y <= viewSize.Y / 2) uncovered |= CameraCoverageEdges.Top;
            if (point.Y >= viewSize.Y / 2) uncovered |= CameraCoverageEdges.Bottom;
        }
        return new CameraCoverageResult(uncovered == CameraCoverageEdges.None, uncovered);
    }

    private static bool ContainsConvex(DirectionPoint[] polygon, DirectionPoint point)
    {
        const float tolerance = .01f;
        float? sign = null;
        for (var index = 0; index < polygon.Length; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Length];
            var cross = (b.X - a.X) * (point.Y - a.Y) -
                (b.Y - a.Y) * (point.X - a.X);
            if (MathF.Abs(cross) <= tolerance) continue;
            var current = MathF.Sign(cross);
            if (sign is not null && current != sign) return false;
            sign = current;
        }
        return true;
    }
}
