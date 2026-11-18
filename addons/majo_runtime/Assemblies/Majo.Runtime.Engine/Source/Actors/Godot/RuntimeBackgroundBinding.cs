#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Actors.Godot;

/// <summary>The Runtime host owns layout; content keeps its native coordinates.</summary>
internal sealed class RuntimeBackgroundBinding : IDisposable
{
    private readonly IBackgroundActor actor;
    private readonly Control host;
    private readonly Node2D placement = new() { Name = "BackgroundPlacement" };
    private IBackgroundContent? content;
    private IDisposable? lease;
    private bool disposed;

    public RuntimeBackgroundBinding(IBackgroundActor actor, Control host)
    {
        this.actor = actor;
        this.host = host;
        actor.SceneNode.AddChild(placement);
        actor.ContentChanged += Refresh;
        host.Resized += Layout;
        try { Refresh(); }
        catch { Dispose(); throw; }
    }

    private void Refresh()
    {
        if (ReferenceEquals(content, actor.CurrentContent)) return;
        var next = actor.CurrentContent;
        IDisposable? nextLease = null;
        try
        {
            if (next is not null)
            {
                GodotBackgroundContent.ValidateBounds(next.LocalBounds);
                nextLease = next.Retain();
                next.Root.GetParent()?.RemoveChild(next.Root);
                placement.AddChild(next.Root);
            }
        }
        catch { nextLease?.Dispose(); throw; }
        var previous = content;
        var previousLease = lease;
        content = next;
        lease = nextLease;
        try
        {
            if (previous is not null && GodotObject.IsInstanceValid(previous.Root) && previous.Root.GetParent() == placement)
            {
                placement.RemoveChild(previous.Root);
                actor.SceneNode.AddChild(previous.Root);
            }
            Layout();
        }
        finally { previousLease?.Dispose(); }
    }

    private void Layout()
    {
        if (content is null || host.Size.X <= 0 || host.Size.Y <= 0) return;
        placement.Transform = DirectionLayoutCalculator.CalculateBackgroundTransform(
            host.Size, content.LocalBounds, BackdropFit.Cover);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        actor.ContentChanged -= Refresh;
        if (GodotObject.IsInstanceValid(host)) host.Resized -= Layout;
        try
        {
            if (content is not null && GodotObject.IsInstanceValid(content.Root) && content.Root.GetParent() == placement)
            {
                placement.RemoveChild(content.Root);
                actor.SceneNode.AddChild(content.Root);
            }
        }
        finally
        {
            content = null;
            try { lease?.Dispose(); }
            finally
            {
                lease = null;
                if (GodotObject.IsInstanceValid(placement)) placement.QueueFree();
            }
        }
    }
}
