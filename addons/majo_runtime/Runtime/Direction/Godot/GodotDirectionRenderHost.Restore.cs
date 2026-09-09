#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed partial class GodotDirectionRenderHost
{
    private long restoreGeneration;
    private RetainedOutput? retainedOutput;

    internal IRetainedRestoreOutput RetainRestoreOutput()
    {
        EnsureInitialized();
        if (retainedOutput is not null)
            throw new InvalidOperationException("Direction output is already retained by a restore.");
        // Copy textures, never retain a ViewportTexture that subsequent rendering overwrites.
        var candidate = new RetainedOutput(this, ++restoreGeneration);
        retainedOutput = candidate;
        InvalidateGenerations();
        return candidate;
    }

    private sealed class RetainedOutput : IRetainedRestoreOutput
    {
        private readonly GodotDirectionRenderHost host;
        private readonly long generation;
        private readonly CanvasLayer layer;
        private readonly List<ImageTexture> textures = new();
        private bool disposed;

        internal RetainedOutput(GodotDirectionRenderHost host, long generation)
        {
            this.host = host;
            this.generation = generation;
            layer = new CanvasLayer { Name = "MajoRetainedDirectionOutput", Layer = 6 };
            try
            {
                // Dummy rendering has no images. Its tests exercise only logical ownership.
                layer.Visible = host.OutputLayer.Visible && DisplayServer.GetName() != "headless";
                if (layer.Visible)
                {
                    Copy(host.LiveOutput);
                    Copy(host.ScreenTransitionSource);
                    layer.AddChild(new ColorRect
                    {
                        Position = host.Curtain.Position,
                        Size = host.Curtain.Size,
                        Color = host.Curtain.Color,
                        Visible = host.Curtain.Visible,
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                    });
                }
                // A sibling of the render host output, outside Reset/scene resource ownership.
                host.owner.AddChild(layer);
            }
            catch
            {
                layer.Free();
                foreach (var texture in textures) texture.Dispose();
                throw;
            }
        }

        private void Copy(TextureRect source)
        {
            if (!source.Visible || source.Texture is null) return;
            using var image = source.Texture.GetImage();
            if (image is null || image.IsEmpty())
                throw new InvalidOperationException("Cannot retain Direction output before its first rendered image.");
            var texture = ImageTexture.CreateFromImage(image);
            textures.Add(texture);
            var copy = CreateTextureRect(source.Name + "Frozen");
            copy.Position = source.Position;
            copy.Size = source.Size;
            copy.Texture = texture;
            copy.Modulate = source.Modulate;
            copy.SelfModulate = source.SelfModulate;
            layer.AddChild(copy);
        }

        public async Task WaitForTargetAsync(CancellationToken cancellationToken)
        {
            Validate();
            cancellationToken.ThrowIfCancellationRequested();
            if (DisplayServer.GetName() == "headless") return;
            host.ActivateViewports();
            // Observe an entire render submission after commit. Godot orders the active
            // Viewport texture dependency graph (content -> view -> screen) in that submission.
            // ProcessFrame and visual settlement do not establish this rendering fence.
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var submitted = false;
            void BeforeDraw() => submitted = true;
            void AfterDraw()
            {
                if (submitted) complete.TrySetResult();
            }
            RenderingServer.FramePreDraw += BeforeDraw;
            RenderingServer.FramePostDraw += AfterDraw;
            try { await complete.Task.WaitAsync(cancellationToken); }
            finally
            {
                RenderingServer.FramePreDraw -= BeforeDraw;
                RenderingServer.FramePostDraw -= AfterDraw;
            }
            Validate();
            using var target = host.ScreenViewport.GetTexture().GetImage();
            if (target is null || target.IsEmpty() || target.GetSize() != host.ScreenViewport.Size)
                throw new InvalidOperationException("Direction target frame is unavailable after rendering.");
        }

        private void Validate()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (host.restoreGeneration != generation || !ReferenceEquals(host.retainedOutput, this))
                throw new InvalidOperationException("Direction restore output generation is stale.");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (GodotObject.IsInstanceValid(layer))
            {
                layer.Visible = false;
                layer.Free();
            }
            foreach (var texture in textures) texture.Dispose();
            textures.Clear();
            if (ReferenceEquals(host.retainedOutput, this)) host.retainedOutput = null;
        }
    }
}
