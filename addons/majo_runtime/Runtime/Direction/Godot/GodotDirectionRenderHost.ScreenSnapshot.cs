#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Direction.Godot;

internal sealed partial class GodotDirectionRenderHost
{
    internal bool UseGpuScreenSnapshots { get; set; } =
        Array.IndexOf(OS.GetCmdlineUserArgs(), "--majo-gpu-screen-snapshots") >= 0;

    internal async Task<GodotVisualInput> CaptureScreenTransitionAsync(CancellationToken token)
    {
        EnsureInitialized();
        token.ThrowIfCancellationRequested();
        // Compatibility has no RenderingDevice. Keep its existing capture path;
        // the experiment changes Screen transitions only, not screenshot/restore APIs.
        if (!UseGpuScreenSnapshots || DisplayServer.GetName() == "headless" ||
            RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility")
        {
            var texture = await CaptureScreenAsync(token);
            return GodotVisualInput.Frozen(texture, new Rect2(Vector2.Zero, texture.GetSize()), release: texture.Dispose);
        }

        using var timing = Diagnostics.RuntimePerformanceTrace.Source.StartActivity("direction.screen.gpu-snapshot.async");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, renderCancellation.Token);
        using var demand = AcquireScreenDemand();
        while (true)
        {
            await WaitForRenderedScreenAsync(lifetime.Token);
            var revision = renderSizeRevision;
            var snapshot = await GodotGpuTextureSnapshot.CopyAsync(ScreenViewport.GetTexture(), lifetime.Token);
            if (!lifetime.IsCancellationRequested && revision == renderSizeRevision) return snapshot;
            snapshot.Dispose();
            lifetime.Token.ThrowIfCancellationRequested();
        }
    }
}
