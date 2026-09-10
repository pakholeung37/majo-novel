#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Resources;
using Majo.Runtime.Resources.Godot;

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
        cancellationToken.ThrowIfCancellationRequested();
        // Preserve the script's 15 × 1024 logical strip (13 seats + two guards).
        // Canvas drawing references the original wall for each panel; no readback,
        // resampling, panorama allocation, or additional GPU texture upload.
        var texture = new HorizontalRepeatTexture2D(lease.Resource!, new Vector2I(1024, 1024), 15);
        try { context.Resources.Register<Texture2D>(key, texture, value => value.Dispose()); }
        catch { texture.Dispose(); throw; }
    }
}
