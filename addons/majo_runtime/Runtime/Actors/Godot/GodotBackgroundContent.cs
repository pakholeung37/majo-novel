#nullable enable

using System;
using System.Collections.Generic;
using Godot;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Owns a content node and its resource scope. Use and release on the Godot thread.</summary>
public sealed class GodotBackgroundContent : IBackgroundContent, IDisposable
{
    private readonly BackgroundContentLifetime lifetime;

    public GodotBackgroundContent(CanvasItem root, Rect2 localBounds, BackgroundCoverage coverage,
        IDisposable? resources = null, Action? released = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidateBounds(localBounds);
        if (!Enum.IsDefined(coverage)) throw new ArgumentOutOfRangeException(nameof(coverage));
        Root = root;
        LocalBounds = localBounds;
        Coverage = coverage;
        lifetime = new BackgroundContentLifetime(() =>
        {
            var failures = new List<Exception>();
            try
            {
                if (GodotObject.IsInstanceValid(root))
                {
                    root.GetParent()?.RemoveChild(root);
                    root.QueueFree();
                }
            }
            catch (Exception error) { failures.Add(error); }
            try { resources?.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            try { released?.Invoke(); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("Background content cleanup failed.", failures);
        });
    }

    /// <summary>Opt-in immutable texture presentation. The root is only used by Runtime bindings;
    /// Direction draws the texture directly. Node-based animated content uses the normal constructor.</summary>
    public static GodotBackgroundContent FromTexture(Texture2D texture, IDisposable? resources = null, Action? released = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return new GodotBackgroundContent(new Sprite2D { Name = "Appearance", Texture = texture, Centered = false },
            new Rect2(Vector2.Zero, texture.GetSize()),
            texture.HasAlpha() ? BackgroundCoverage.Unknown : BackgroundCoverage.OpaqueRectangle, resources, released)
        { DirectTexture = texture };
    }

    internal Texture2D? DirectTexture { get; private init; }
    public CanvasItem Root { get; }
    public Rect2 LocalBounds { get; }
    public BackgroundCoverage Coverage { get; }
    public IDisposable Retain() => lifetime.Retain();
    public void Dispose() => lifetime.Dispose();

    internal void DetachFrom(Node owner)
    {
        if (GodotObject.IsInstanceValid(Root) && owner.IsAncestorOf(Root))
            Root.GetParent()?.RemoveChild(Root);
    }

    public static void ValidateBounds(Rect2 bounds)
    {
        if (!bounds.Position.IsFinite() || !bounds.Size.IsFinite() ||
            bounds.Size.X <= 0 || bounds.Size.Y <= 0 || !bounds.End.IsFinite())
            throw new ArgumentOutOfRangeException(nameof(bounds), "Background bounds must be finite and have positive dimensions.");
    }
}
