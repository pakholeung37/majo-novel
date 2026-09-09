#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Rendering;

public static class DirectionLayoutCalculator
{
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

    public static Rect2 CalculateBackdropBounds(Vector2I viewportSize, BackdropPlacement? placement, Vector2 textureSize)
    {
        if (placement is null)
        {
            return new Rect2(Vector2.Zero, viewportSize);
        }

        var size = textureSize * placement.Scale;
        var at = Majo.Runtime.Actors.Godot.StageCoordinates.ToPixels(
            new Vector2(placement.At.X, placement.At.Y), viewportSize);
        return new Rect2(at - new Vector2(placement.Origin.X, placement.Origin.Y) * size, size);
    }

    public static TextureRect.StretchModeEnum ResolveBackdropStretchMode(BackdropFit fit) =>
        fit switch
        {
            BackdropFit.Cover => TextureRect.StretchModeEnum.KeepAspectCovered,
            BackdropFit.Contain => TextureRect.StretchModeEnum.KeepAspectCentered,
            BackdropFit.Stretch => TextureRect.StretchModeEnum.Scale,
            _ => throw new ArgumentOutOfRangeException(nameof(fit)),
        };

    public static Color ToGodotColor(DirectionColor color) =>
        new(color.R, color.G, color.B, color.A);
}
