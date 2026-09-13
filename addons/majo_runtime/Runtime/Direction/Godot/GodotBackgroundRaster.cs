#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

/// <summary>An output surface, never a restriction on the actor's content type.
/// Live surfaces simulate once per Stage. Frozen surfaces own a GPU readback.</summary>
internal sealed class GodotBackgroundRaster : IDisposable
{
    private static readonly HashSet<GodotBackgroundRaster> instances = new();
    internal static IReadOnlyCollection<GodotBackgroundRaster> Instances => instances;
    internal Node Owner { get; }
    internal bool IsSnapshot => ownsTexture;
    internal long EstimatedBytes => (long)Math.Ceiling(Bounds.Size.X) * (long)Math.Ceiling(Bounds.Size.Y) * 4;
    private readonly IBackgroundContent? content;
    private IDisposable? lease;
    private readonly SubViewport? viewport;
    private readonly Node2D? origin;
    private readonly bool ownsTexture;
    private bool disposed;
    private readonly GodotVisualInput input;
    public Texture2D Texture { get; }
    public Rect2 Bounds { get; }
    public BackgroundCoverage Coverage { get; }
    public static int LiveCount { get; private set; }
    public static int SnapshotCount { get; private set; }

    private GodotBackgroundRaster(Node owner, GodotVisualInput frozen, Rect2 bounds, BackgroundCoverage coverage)
    {
        Owner = owner;
        input = frozen;
        Texture = frozen.Texture;
        Bounds = bounds;
        Coverage = coverage;
        ownsTexture = true;
        SnapshotCount++;
        instances.Add(this);
    }

    public GodotBackgroundRaster(Node parent, IBackgroundContent content)
    {
        ValidateTree(content.Root);
        GodotBackgroundContent.ValidateBounds(content.LocalBounds);
        if (content.LocalBounds.Size.X > 16384 || content.LocalBounds.Size.Y > 16384)
            throw new DirectionException("Background content exceeds the 16384 pixel render surface limit.");
        Owner = parent;
        this.content = content;
        Bounds = content.LocalBounds;
        Coverage = content.Coverage;
        lease = content.Retain();
        viewport = new SubViewport
        {
            Name = "BackgroundContentSurface",
            TransparentBg = true,
            Disable3D = true,
            Size = new Vector2I(Math.Max(1, (int)Math.Ceiling(Bounds.Size.X)), Math.Max(1, (int)Math.Ceiling(Bounds.Size.Y))),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            CanvasCullMask = GodotDirectionRenderHost.BackgroundVisibilityLayer,
        };
        var rasterScale = (Vector2)viewport.Size / Bounds.Size;
        origin = new Node2D { Position = -Bounds.Position * rasterScale, Scale = rasterScale };
        viewport.AddChild(origin);
        try
        {
            parent.AddChild(viewport);
            content.Root.GetParent()?.RemoveChild(content.Root);
            origin.AddChild(content.Root);
            content.Root.Visible = true;
            GodotDirectionRenderHost.AssignVisibilityLayer(origin, GodotDirectionRenderHost.BackgroundVisibilityLayer);
            viewport.TreeExiting += Dispose;
            viewport.GetTree().NodeAdded += OnNodeAdded;
            input = GodotVisualInput.Live(viewport, Bounds);
            Texture = input.Texture;
            LiveCount++;
            instances.Add(this);
        }
        catch
        {
            if (content.Root.GetParent() == origin) origin.RemoveChild(content.Root);
            viewport.QueueFree();
            lease.Dispose();
            lease = null;
            throw;
        }
    }

    private void OnNodeAdded(Node node)
    {
        if (origin is null || !origin.IsAncestorOf(node)) return;
        try { ValidateTree(node); }
        catch (DirectionException error)
        {
            // A dynamically inserted escape must never draw into another Stage or the global canvas.
            if (node is CanvasLayer layer) layer.Visible = false;
            if (node is CanvasItem item) item.Visible = false;
            node.ProcessMode = Node.ProcessModeEnum.Disabled;
            node.QueueFree();
            GD.PushError(error.Message);
            return;
        }
        GodotDirectionRenderHost.AssignVisibilityLayer(node, GodotDirectionRenderHost.BackgroundVisibilityLayer);
    }

    internal static void ValidateTree(Node node)
    {
        if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion())
            throw new DirectionException("Background content must contain live nodes.");
        if (node is CanvasLayer or SubViewport || node is CanvasItem { TopLevel: true })
            throw new DirectionException("Background content cannot bypass the host with CanvasLayer, SubViewport or top-level CanvasItems.");
        foreach (var child in node.GetChildren()) ValidateTree(child);
    }

    public void SetLiveVisible(bool value)
    {
        if (content is not null && GodotObject.IsInstanceValid(content.Root)) content.Root.Visible = value;
    }

    public Task ReadyAsync(CancellationToken token) => input.ReadyAsync(token);

    public GodotBackgroundRaster Freeze()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new GodotBackgroundRaster(Owner, input.Freeze(), Bounds, Coverage);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        instances.Remove(this);
        input.Dispose();
        if (ownsTexture) { SnapshotCount--; return; }
        LiveCount--;
        try
        {
            if (viewport is not null && GodotObject.IsInstanceValid(viewport))
            {
                viewport.TreeExiting -= Dispose;
                if (viewport.IsInsideTree()) viewport.GetTree().NodeAdded -= OnNodeAdded;
            }
            if (content is not null && GodotObject.IsInstanceValid(content.Root) && content.Root.GetParent() == origin)
                origin!.RemoveChild(content.Root);
        }
        finally
        {
            try { lease?.Dispose(); }
            finally
            {
                lease = null;
                if (GodotObject.IsInstanceValid(viewport)) viewport!.QueueFree();
            }
        }
    }
}
