#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

/// <summary>Independent frozen GPU texture. RD access runs on the render thread; dispose inputs on the Godot thread.</summary>
internal static class GodotGpuTextureSnapshot
{
    private static int activeCount;
    internal static int ActiveCount => Volatile.Read(ref activeCount);

    internal static Task<GodotVisualInput> CopyAsync(Texture2D source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sourceRid = source.GetRid();
        var completion = new TaskCompletionSource<GodotVisualInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            RenderingDevice? device = null;
            Rid copy = default;
            Texture2Drd? texture = null;
            try
            {
                token.ThrowIfCancellationRequested();
                using var timing = Diagnostics.RuntimePerformanceTrace.Source.StartActivity("direction.screen.gpu-copy.enqueue.sync");
                device = RenderingServer.GetRenderingDevice() ??
                    throw new DirectionException("GPU Screen snapshots require a RenderingDevice renderer.");
                var original = RenderingServer.TextureGetRdTexture(sourceRid);
                if (!original.IsValid) throw new DirectionException("Screen has no GPU texture to capture.");
                using var originalFormat = device.TextureGetFormat(original);
                if ((originalFormat.UsageBits & RenderingDevice.TextureUsageBits.CanCopyFromBit) == 0)
                    throw new DirectionException("Screen GPU texture does not support copying.");
                using var format = new RDTextureFormat
                {
                    Format = originalFormat.Format,
                    Width = originalFormat.Width,
                    Height = originalFormat.Height,
                    Depth = 1,
                    ArrayLayers = 1,
                    Mipmaps = 1,
                    TextureType = RenderingDevice.TextureType.Type2D,
                    UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                        RenderingDevice.TextureUsageBits.CanCopyToBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
                };
                using var view = new RDTextureView();
                copy = device.TextureCreate(format, view);
                if (!copy.IsValid) throw new DirectionException("Unable to allocate Screen GPU snapshot.");
                var error = device.TextureCopy(original, copy, Vector3.Zero, Vector3.Zero,
                    new Vector3(format.Width, format.Height, 1), 0, 0, 0, 0);
                if (error != Error.Ok) throw new DirectionException($"Screen GPU copy failed: {error}.");
                texture = new Texture2Drd { TextureRdRid = copy };
                var ownedTexture = texture;
                var ownedCopy = copy;
                var ownedDevice = device;
                var input = GodotVisualInput.Frozen(texture,
                    new Rect2(Vector2.Zero, new Vector2(format.Width, format.Height)), release: () =>
                    {
                        // Resource changes can notify CanvasItems: detach on the Godot
                        // thread, then queue the RD allocation's destruction behind it.
                        ownedTexture.TextureRdRid = default;
                        ownedTexture.Dispose();
                        RenderingServer.CallOnRenderThread(Callable.From(() =>
                        {
                            ownedDevice.FreeRid(ownedCopy);
                            Interlocked.Decrement(ref activeCount);
                        }));
                    });
                Interlocked.Increment(ref activeCount);
                completion.SetResult(input);
            }
            catch (Exception error)
            {
                if (device is not null) Release(device, texture, copy);
                if (error is OperationCanceledException) completion.TrySetCanceled(token);
                else completion.TrySetException(error);
            }
        }));
        // Do not abandon this task on cancellation: the caller must receive and
        // dispose any allocation that won the race with cancellation.
        return completion.Task;
    }

    private static void Release(RenderingDevice device, Texture2Drd? texture, Rid copy)
    {
        if (texture is not null)
        {
            texture.TextureRdRid = default;
            texture.Dispose();
        }
        if (copy.IsValid) device.FreeRid(copy);
    }
}
