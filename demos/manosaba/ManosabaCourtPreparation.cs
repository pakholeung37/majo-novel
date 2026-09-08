#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Resources;

namespace Majo.Demos.Manosaba;

/// <summary>The thirteen-panel court is demo content, not a backdrop command feature.</summary>
public static class ManosabaCourtPreparation
{
    public static async Task PrepareAsync(ScenePreparationContext context, CancellationToken cancellationToken)
    {
        var key = new ResourceKey("backgrounds/Court_Trial00");
        if (context.Resources.Exists<Texture2D>(key)) return;
        using var lease = context.Resources.LeaseAsync<Texture2D>(new ResourceKey("backgrounds/Court_Wall"),
            cancellationToken: cancellationToken);
        await lease.Completion;
        var source = lease.Resource!;
        using var tile = source.GetImage();
        if (tile is null || tile.IsEmpty()) throw new InvalidOperationException("Court wall has no readable image.");
        if (tile.IsCompressed()) tile.Decompress();
        tile.Convert(Image.Format.Rgba8);
        // Thirteen seats plus one guard panel at either end; 15 × 1024 fits the demo's texture budget; stage scale remains a script choice.
        tile.Resize(1024, 1024, Image.Interpolation.Lanczos);
        using var panorama = Image.CreateEmpty(15 * 1024, 1024, false, Image.Format.Rgba8);
        for (var seat = 0; seat < 15; seat++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            panorama.BlitRect(tile, new Rect2I(0, 0, 1024, 1024), new Vector2I(seat * 1024, 0));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var texture = ImageTexture.CreateFromImage(panorama);
        try { context.Resources.Register<Texture2D>(key, texture, value => value.Dispose()); }
        catch { texture.Dispose(); throw; }
    }
}
