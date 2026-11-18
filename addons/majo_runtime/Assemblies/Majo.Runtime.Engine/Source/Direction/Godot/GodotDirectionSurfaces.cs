#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Majo.Runtime.Direction.Core;
using static Majo.Runtime.Direction.Godot.GodotDirectionRenderNodes;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionSurfaces : IDisposable
{
    private readonly Node owner;
    private readonly GodotDirectionRenderNodes nodes;
    private readonly int maxStages;
    private readonly Dictionary<StageId, GodotDirectionStageSurface> stages = new();
    private readonly Dictionary<ViewId, GodotDirectionViewSurface> views = new();
    private readonly HashSet<GodotDirectionViewSurface> liveViews = new();
    private SubViewport? screenViewport;
    private bool disposed;

    internal GodotDirectionSurfaces(Node owner, GodotDirectionRenderNodes nodes, int maxStages)
    {
        this.owner = owner;
        this.nodes = nodes;
        this.maxStages = maxStages;
        var stage = CreateStage(StageId.Main, "MajoDirectionStageViewport");
        stages.Add(stage.Id, stage);
        var view = CreateView(ViewId.Main, "MajoDirectionViewViewport");
        views.Add(view.Id, view);
        MainStage = stage;
        MainView = view;
    }

    internal GodotDirectionStageSurface MainStage { get; }
    internal GodotDirectionViewSurface MainView { get; }
    internal IEnumerable<GodotDirectionStageSurface> Stages => stages.Values;
    internal IEnumerable<GodotDirectionViewSurface> Views => views.Values;
    internal IEnumerable<GodotDirectionViewSurface> RenderViews => views.Values.Concat(liveViews);
    internal IReadOnlyCollection<StageId> StageIds => stages.Keys;
    internal IReadOnlyCollection<ViewId> ViewIds => views.Keys;
    internal IReadOnlyCollection<GodotDirectionViewSurface> LiveViews => liveViews;

    internal void Attach()
    {
        owner.AddChild(MainStage.Viewport);
        owner.AddChild(MainView.Viewport);
    }

    internal void BindRenderDependencies(SubViewport screen)
    {
        screenViewport = screen;
        RenderingServer.ViewportSetParentViewport(MainView.Viewport.GetViewportRid(), screen.GetViewportRid());
        BindStageRenderDependency(MainStage.Viewport);
    }

    private static GodotDirectionStageSurface CreateStage(StageId stageId, string name)
    {
        var viewport = CreateViewport(name);
        var canvas = new Control
        {
            Name = "StageCanvas",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var background = new ColorRect
        {
            Name = "StageBackground",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var backdrop = new GodotDirectionBackdropVisual { Name = "Backdrop" };
        var transition = new GodotDirectionBackdropVisual { Name = "BackdropTransition" };
        transition.Visible = false;
        var actors = new Node2D { Name = "Actors" };
        canvas.AddChild(background);
        canvas.AddChild(backdrop);
        canvas.AddChild(transition);
        canvas.AddChild(actors);
        viewport.AddChild(canvas);
        var surface = new GodotDirectionStageSurface(
            stageId, viewport, canvas, background, backdrop, transition, actors);
        canvas.VisibilityLayer = GodotDirectionRenderLayers.Stage;
        surface.BackdropFxRoot.VisibilityLayer =
            GodotDirectionRenderLayers.Background | GodotDirectionRenderLayers.BackgroundTransition;
        AssignVisibilityLayer(background, GodotDirectionRenderLayers.Background);
        AssignVisibilityLayer(backdrop, GodotDirectionRenderLayers.Background);
        AssignVisibilityLayer(transition, GodotDirectionRenderLayers.BackgroundTransition);
        AssignVisibilityLayer(actors, GodotDirectionRenderLayers.Subject);
        return surface;
    }

    internal GodotDirectionStageSurface GetOrCreateStage(StageId id)
    {
        if (stages.TryGetValue(id, out var existing)) return existing;
        if (stages.Count >= maxStages)
            throw new DirectionException($"Direction Stage limit of {maxStages} was exceeded while creating '{id}'.");
        var surface = CreateStage(id, $"MajoDirectionStageViewport-{stages.Count}");
        stages.Add(id, surface);
        owner.AddChild(surface.Viewport);
        BindStageRenderDependency(surface.Viewport);
        ResizeStage(surface, MainStage.Viewport.Size);
        return surface;
    }

    private GodotDirectionViewSurface CreateView(ViewId viewId, string name)
    {
        var viewport = CreateViewport(name);
        var canvas = new Control
        {
            Name = "ViewCanvas",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var transform = new Node2D { Name = "ViewTransform" };
        var content = nodes.CreateTextureRect("ViewContent");
        var viewTransition = nodes.CreateTextureRect("ViewTransition");
        viewTransition.Visible = false;
        transform.AddChild(content);
        canvas.AddChild(transform);
        canvas.AddChild(viewTransition);
        viewport.AddChild(canvas);
        var surface = new GodotDirectionViewSurface(viewId, viewport, canvas, transform, content, viewTransition);
        return surface;
    }

    internal GodotDirectionViewSurface GetOrCreateView(ViewId id, out bool created)
    {
        created = false;
        if (views.TryGetValue(id, out var existing)) return existing;
        var surface = CreateView(id, $"MajoDirectionViewViewport-{views.Count}");
        views.Add(id, surface);
        owner.AddChild(surface.Viewport);
        RenderingServer.ViewportSetParentViewport(surface.Viewport.GetViewportRid(), screenViewport!.GetViewportRid());
        ResizeView(surface, MainView.Viewport.Size);
        created = true;
        return surface;
    }

    internal bool HasStage(StageId id) => stages.ContainsKey(id);
    internal bool HasView(ViewId id) => views.ContainsKey(id);
    internal bool TryGetStage(StageId id, out GodotDirectionStageSurface surface) => stages.TryGetValue(id, out surface!);
    internal bool TryGetView(ViewId id, out GodotDirectionViewSurface surface) => views.TryGetValue(id, out surface!);
    internal GodotDirectionStageSurface RequireStage(StageId id) => stages.TryGetValue(id, out var surface)
        ? surface : throw new DirectionReferenceException($"Direction Stage '{id}' has no prepared render surface.");
    internal GodotDirectionViewSurface RequireView(ViewId id) => views.TryGetValue(id, out var surface)
        ? surface : throw new DirectionReferenceException($"Direction View '{id}' has no prepared render surface.");

    internal void PromoteToLive(GodotDirectionViewSurface view)
    {
        views.Remove(view.Id);
        liveViews.Add(view);
    }
    internal bool ReleaseLive(GodotDirectionViewSurface view)
    {
        if (!liveViews.Remove(view)) return false;
        ReleaseViewSurface(view);
        return true;
    }
    internal void ReleaseStage(StageId id)
    {
        if (id != StageId.Main && stages.Remove(id, out var surface)) QueueFree(surface.Viewport);
    }
    internal void ReleaseView(ViewId id)
    {
        if (id != ViewId.Main && views.Remove(id, out var surface)) ReleaseViewSurface(surface);
    }
    internal void ResetSecondary()
    {
        foreach (var id in stages.Keys.Where(id => id != StageId.Main).ToArray()) ReleaseStage(id);
        foreach (var id in views.Keys.Where(id => id != ViewId.Main).ToArray()) ReleaseView(id);
        foreach (var view in liveViews.ToArray()) ReleaseLive(view);
    }
    internal void Resize(Vector2I logicalSize, Vector2I rasterSize)
    {
        foreach (var stage in stages.Values) ResizeStage(stage, logicalSize);
        foreach (var view in RenderViews) ResizeView(view, rasterSize);
    }
    private static void ResizeView(GodotDirectionViewSurface view, Vector2I size)
    {
        view.Viewport.Size = size;
        view.ContentViewport.Size = size;
        view.BackgroundViewport.Size = size;
        view.TransitionBackgroundViewport.Size = size;
        view.CompositeViewport.Size = size;
        foreach (var control in new Control[] { view.StageBase, view.Output, view.BackgroundOutput,
            view.TransitionBackgroundOutput, view.TransitionBlendOutput, view.CompositeOutput,
            view.Canvas, view.Content, view.TransitionSource }) SetFullRect(control, size);
    }
    private void BindStageRenderDependency(SubViewport viewport)
    {
        // Views draw the shared Stage World2D directly. Actor-owned render targets
        // (for example Live2D clipping masks) must finish before ANY View draws it.
        // Anchor every Stage below the permanent main View's content pass: Godot
        // sorts viewports by parent depth, placing Stages and their descendants
        // ahead of all equally deep View content/background passes, including
        // secondary Views. Parenting only to Screen or View is not deep enough.
        RenderingServer.ViewportSetParentViewport(
            viewport.GetViewportRid(),
            views[ViewId.Main].ContentViewport.GetViewportRid());
    }

    private static void ResizeStage(GodotDirectionStageSurface surface, Vector2I resolution)
    {
        surface.Viewport.Size = resolution;
        SetFullRect(surface.Canvas, resolution);
        SetFullRect(surface.Background, resolution);
        surface.Backdrop.Set(surface.Backdrop.Raster, surface.PublishedBackdrop, resolution);
        surface.BackdropTransition.Set(surface.BackdropTransition.Raster, surface.TransitionBackdrop, resolution);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var view in RenderViews.ToArray()) ReleaseViewSurface(view);
        foreach (var stage in stages.Values) QueueFree(stage.Viewport);
        views.Clear();
        liveViews.Clear();
        stages.Clear();
        screenViewport = null;
    }

    private static void ReleaseViewSurface(GodotDirectionViewSurface view)
    {
        QueueFree(view.Viewport);
        // TreeExiting normally owns this material. A never-attached surface
        // does not emit that signal, so its owner must also cover cold disposal.
        if (GodotObject.IsInstanceValid(view.RasterMaterial)) view.RasterMaterial.Dispose();
    }
}
