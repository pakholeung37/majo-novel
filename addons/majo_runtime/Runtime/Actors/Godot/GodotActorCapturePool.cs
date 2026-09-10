#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Scene-owned reusable render targets. Leased captures are never resized or shared.</summary>
internal sealed partial class GodotActorCapturePool : Node
{
    private const int RetainedPairs = 2;
    private static readonly ConditionalWeakTable<Node, GodotActorCapturePool> Pools = new();
    private readonly List<Capture> captures = new();
    private Vector2I capacity = Vector2I.One;

    private GodotActorCapturePool(Node scene)
    {
        Name = "ActorAppearanceCaptures";
        scene.AddChild(this);
    }

    public static GodotActorCapturePool For(Node owner)
    {
        var tree = owner.GetTree();
        return Pools.GetValue(tree.CurrentScene ?? tree.Root, scene => new GodotActorCapturePool(scene));
    }

    public Capture Acquire(CanvasItem source, CanvasItem target, Rect2 bounds)
    {
        var needed = (Vector2I)bounds.Size.Ceil();
        capacity = new Vector2I(Math.Max(capacity.X, needed.X), Math.Max(capacity.Y, needed.Y));
        var capture = captures.Find(value => !value.Leased);
        if (capture is null)
        {
            capture = new Capture(this, this);
            captures.Add(capture);
        }
        capture.Leased = true;
        capture.SetContent(source, target, bounds, capacity);
        return capture;
    }

    private void Release(Capture capture)
    {
        capture.Clear();
        capture.Leased = false;
        if (captures.Count <= RetainedPairs) return;
        captures.Remove(capture);
        capture.Source.QueueFree();
        capture.Target.QueueFree();
    }

    internal sealed class Capture
    {
        private readonly GodotActorCapturePool pool;
        public SubViewport Source { get; }
        public SubViewport Target { get; }
        public bool Leased { get; set; }

        public Capture(GodotActorCapturePool pool, Node root)
        {
            this.pool = pool;
            Source = Create("AppearanceSource");
            Target = Create("AppearanceTarget");
            root.AddChild(Source);
            root.AddChild(Target);
        }

        public void SetContent(CanvasItem source, CanvasItem target, Rect2 bounds, Vector2I size)
        {
            Attach(Source, source, bounds, size);
            Attach(Target, target, bounds, size);
        }

        public void Release() { if (Leased) pool.Release(this); }

        public void Clear()
        {
            foreach (var viewport in new[] { Source, Target })
            {
                if (!GodotObject.IsInstanceValid(viewport)) continue;
                viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                foreach (var child in viewport.GetChildren()) child.Free();
            }
        }

        private static SubViewport Create(string name) => new()
        {
            Name = name,
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };

        private static void Attach(SubViewport viewport, CanvasItem content, Rect2 bounds, Vector2I size)
        {
            if (viewport.Size != size) viewport.Size = size;
            // Preserve the logical rectangle and normalized UVs expected by
            // transition shaders, even when reusing a larger backing texture.
            viewport.Size2DOverride = (Vector2I)bounds.Size.Ceil();
            viewport.Size2DOverrideStretch = true;
            var offset = new Node2D { Position = -bounds.Position };
            viewport.AddChild(offset);
            offset.AddChild(content);
            content.Visible = true;
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
    }
}
