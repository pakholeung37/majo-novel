#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Rendering;

public static class DirectionLayoutCalculator
{
    public readonly record struct BackgroundLayout(Transform2D Transform, Rect2 ClipRect, Rect2 VisibleBounds);

    public static BackgroundLayout CalculateBackgroundLayout(Vector2 viewportSize, Rect2 bounds, BackdropFit fit,
        BackdropPlacement? placement = null)
    {
        var transform = CalculateBackgroundTransform(viewportSize, bounds, fit, placement);
        var mapped = transform * bounds;
        var clip = placement is null ? new Rect2(Vector2.Zero, viewportSize) : mapped;
        return new BackgroundLayout(transform, clip, mapped.Intersection(clip));
    }

    /// <summary>Maps native bounds (including a nonzero origin) into host coordinates.</summary>
    public static Transform2D CalculateBackgroundTransform(Vector2 viewportSize, Rect2 localBounds,
        BackdropFit fit, BackdropPlacement? placement = null)
    {
        Majo.Runtime.Actors.Godot.GodotBackgroundContent.ValidateBounds(localBounds);
        if (!viewportSize.IsFinite() || viewportSize.X <= 0 || viewportSize.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportSize));
        Vector2 scale;
        Vector2 translation;
        if (placement is not null)
        {
            scale = Vector2.One * placement.Scale;
            var at = new Vector2(placement.At.X * viewportSize.X / 100,
                (1 - placement.At.Y / 100) * viewportSize.Y);
            var anchor = localBounds.Position + new Vector2(placement.Origin.X, placement.Origin.Y) * localBounds.Size;
            translation = at - anchor * scale;
        }
        else
        {
            var ratio = viewportSize / localBounds.Size;
            scale = fit switch
            {
                BackdropFit.Cover => Vector2.One * Math.Max(ratio.X, ratio.Y),
                BackdropFit.Contain => Vector2.One * Math.Min(ratio.X, ratio.Y),
                BackdropFit.Stretch => ratio,
                _ => throw new ArgumentOutOfRangeException(nameof(fit)),
            };
            translation = (viewportSize - localBounds.Size * scale) / 2 - localBounds.Position * scale;
        }
        if (!scale.IsFinite() || !translation.IsFinite())
            throw new ArgumentOutOfRangeException(nameof(localBounds), "Background layout must remain finite.");
        return new Transform2D(new Vector2(scale.X, 0), new Vector2(0, scale.Y), translation);
    }

    public static Rect2 CalculateSlotRect(LayoutId layout, LayoutSlotId slot, Vector2I viewportSize)
    {
        var size = new Vector2(viewportSize.X, viewportSize.Y);
        if (layout.Value.Equals("split", StringComparison.OrdinalIgnoreCase))
        {
            return new Rect2(
                slot.Value == "inset" ? new Vector2(size.X / 2, 0) : Vector2.Zero,
                new Vector2(size.X / 2, size.Y));
        }
        if (layout.Value.Equals("pip", StringComparison.OrdinalIgnoreCase) && slot.Value == "inset")
        {
            return new Rect2(size.X * .70f, size.Y * .06f, size.X * .25f, size.Y * .25f);
        }
        return new Rect2(Vector2.Zero, size);
    }

    public static Color ToGodotColor(DirectionColor color) =>
        new(color.R, color.G, color.B, color.A);
}
