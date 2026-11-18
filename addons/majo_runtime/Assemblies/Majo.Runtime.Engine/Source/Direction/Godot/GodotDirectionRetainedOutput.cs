#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionRetainedOutput : IDisposable
{
    private readonly Node owner;
    private readonly GodotDirectionScreenComposer screen;
    private readonly GodotDirectionRenderNodes nodes;
    private readonly Func<CancellationToken, Task> renderTarget;
    private long generation;
    private RetainedOutput? retained;
    private bool disposed;

    internal GodotDirectionRetainedOutput(Node owner, GodotDirectionScreenComposer screen,
        GodotDirectionRenderNodes nodes, Func<CancellationToken, Task> renderTarget)
    {
        this.owner = owner;
        this.screen = screen;
        this.nodes = nodes;
        this.renderTarget = renderTarget;
    }
    internal IRetainedRestoreOutput Retain()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (retained is not null)
            throw new InvalidOperationException("Direction output is already retained by a restore.");
        // Copies survive both mutation and replacement of their source viewports.
        var candidate = new RetainedOutput(this, ++generation);
        retained = candidate;
        return candidate;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        retained?.Dispose();
    }

    private sealed class RetainedOutput : IRetainedRestoreOutput
    {
        private readonly GodotDirectionRetainedOutput owner;
        private readonly long generation;
        private readonly CanvasLayer layer;
        private readonly List<ImageTexture> textures = new();
        private bool disposed;

        internal RetainedOutput(GodotDirectionRetainedOutput owner, long generation)
        {
            this.owner = owner;
            this.generation = generation;
            layer = new CanvasLayer { Name = "MajoRetainedDirectionOutput", Layer = 6 };
            try
            {
                // Dummy rendering has no images. Its tests exercise only logical ownership.
                layer.Visible = owner.screen.OutputLayer.Visible && DisplayServer.GetName() != "headless";
                if (layer.Visible)
                {
                    Copy(owner.screen.LiveOutput);
                    Copy(owner.screen.ScreenTransitionSource);
                    layer.AddChild(new ColorRect
                    {
                        Position = owner.screen.Curtain.Position,
                        Size = owner.screen.Curtain.Size,
                        Color = owner.screen.Curtain.Color,
                        Visible = owner.screen.Curtain.Visible,
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                    });
                }
                // A sibling of the render host output, outside Reset/scene resource ownership.
                owner.owner.AddChild(layer);
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
            var copy = owner.nodes.CreateTextureRect(source.Name + "Frozen");
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
            // Observe an entire render submission after commit. Godot orders the active
            // Viewport texture dependency graph (content -> view -> screen) in that submission.
            // ProcessFrame and visual settlement do not establish this rendering fence.
            await owner.renderTarget(cancellationToken);
            Validate();
            using var target = owner.screen.ScreenViewport.GetTexture().GetImage();
            if (target is null || target.IsEmpty() || target.GetSize() != owner.screen.ScreenViewport.Size)
                throw new InvalidOperationException("Direction target frame is unavailable after rendering.");
        }

        private void Validate()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (owner.generation != generation || !ReferenceEquals(owner.retained, this))
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
            if (ReferenceEquals(owner.retained, this)) owner.retained = null;
        }
    }
}
