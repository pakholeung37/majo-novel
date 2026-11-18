#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Resources.Godot;

/// <summary>
/// A logical 2D strip drawn from one resident texture, without generating an image.
/// Shader samplers/RID consumers see Source; canvas drawing supports the full strip.
/// </summary>
public sealed partial class HorizontalRepeatTexture2D : Texture2D
{
    public Texture2D Source { get; }
    public Vector2I TileSize { get; }
    public int Count { get; }

    public HorizontalRepeatTexture2D(Texture2D source, Vector2I tileSize, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (tileSize.X <= 0 || tileSize.Y <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _ = checked(tileSize.X * count);
        Source = source;
        TileSize = tileSize;
        Count = count;
    }

    public override int _GetWidth() => TileSize.X * Count;
    public override int _GetHeight() => TileSize.Y;
    public override bool _HasAlpha() => Source.HasAlpha();
    public override Rid _GetRid() => Source.GetRid();
    public override Image.Format _GetFormat() => Source.GetFormat();

    public override void _Draw(Rid toCanvasItem, Vector2 pos, Color modulate, bool transpose) =>
        _DrawRect(toCanvasItem, new Rect2(pos, GetSize()), false, modulate, transpose);

    public override void _DrawRect(Rid toCanvasItem, Rect2 rect, bool tile, Color modulate, bool transpose) =>
        DrawPattern(toCanvasItem, rect,
            new Rect2(Vector2.Zero, tile ? (transpose ? new Vector2(rect.Size.Y, rect.Size.X) : rect.Size).Abs() : GetSize()),
            modulate, transpose, true, tile);

    public override void _DrawRectRegion(Rid toCanvasItem, Rect2 rect, Rect2 srcRect,
        Color modulate, bool transpose, bool clipUV) =>
        DrawPattern(toCanvasItem, rect, srcRect, modulate, transpose, clipUV, false);

    private void DrawPattern(Rid toCanvasItem, Rect2 destination, Rect2 requested,
        Color modulate, bool transpose, bool clipUV, bool repeatCanvas)
    {
        if (requested.Size.X <= 0 || requested.Size.Y <= 0) return;
        var visible = repeatCanvas ? requested : requested.Intersection(new Rect2(Vector2.Zero, GetSize()));
        if (!visible.HasArea()) return;
        var firstColumn = Mathf.FloorToInt(visible.Position.X / TileSize.X);
        var lastColumn = Mathf.CeilToInt(visible.End.X / TileSize.X);
        var firstRow = Mathf.FloorToInt(visible.Position.Y / TileSize.Y);
        var lastRow = Mathf.CeilToInt(visible.End.Y / TileSize.Y);
        var sourceScale = Source.GetSize() / (Vector2)TileSize;
        for (var row = firstRow; row < lastRow; row++)
        {
            for (var column = firstColumn; column < lastColumn; column++)
            {
                var origin = new Vector2(column * TileSize.X, row * TileSize.Y);
                var piece = visible.Intersection(new Rect2(origin, TileSize));
                var from = (piece.Position - requested.Position) / requested.Size;
                var to = (piece.End - requested.Position) / requested.Size;
                if (transpose)
                {
                    from = new Vector2(from.Y, from.X);
                    to = new Vector2(to.Y, to.X);
                }
                // Godot uses negative destination dimensions as UV flips,
                // keeping the destination origin (not as a negative extent).
                var size = destination.Size.Abs() * (to - from);
                var position = destination.Position + destination.Size.Abs() * new Vector2(
                    destination.Size.X < 0 ? 1 - to.X : from.X,
                    destination.Size.Y < 0 ? 1 - to.Y : from.Y);
                if (destination.Size.X < 0) size.X = -size.X;
                if (destination.Size.Y < 0) size.Y = -size.Y;
                Source.DrawRectRegion(toCanvasItem, new Rect2(position, size),
                    new Rect2((piece.Position - origin) * sourceScale, piece.Size * sourceScale),
                    modulate, transpose, clipUV);
            }
        }
    }
}
