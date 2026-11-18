#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionBackdropTransitions : IDisposable
{
    private readonly GodotDirectionSurfaces surfaces;
    private readonly GodotDirectionRenderNodes nodes;
    private readonly GodotViewRenderDemand demand;
    private readonly Dictionary<StageId, Prepared> active = new();
    private readonly HashSet<Prepared> prepared = new();

    internal GodotDirectionBackdropTransitions(GodotDirectionSurfaces surfaces, GodotDirectionRenderNodes nodes,
        GodotViewRenderDemand demand)
    {
        this.surfaces = surfaces;
        this.nodes = nodes;
        this.demand = demand;
    }
    internal Prepared Prepare(StageId stageId, DirectionTransitionSpec transition, ITransitionDriver driver, bool activate)
    {
        var result = new Prepared(this, stageId, transition, driver);
        prepared.Add(result);
        try
        {
            foreach (var view in surfaces.RenderViews) result.Track(view);
            if (activate) result.Activate();
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal void Track(GodotDirectionViewSurface view)
    {
        foreach (var mix in active.Values.ToArray()) mix.Track(view);
    }
    internal void Remove(GodotDirectionViewSurface view)
    {
        foreach (var mix in prepared.ToArray()) mix.Remove(view);
    }
    internal void Clear()
    {
        var failures = new List<Exception>();
        foreach (var mix in prepared.ToArray())
            try { mix.Dispose(); } catch (Exception error) { failures.Add(error); }
        active.Clear();
        prepared.Clear();
        if (failures.Count > 0) throw new AggregateException("Background transitions cleanup failed.", failures);
    }
    public void Dispose() => Clear();

    internal sealed class Prepared(
        GodotDirectionBackdropTransitions owner, StageId stageId, DirectionTransitionSpec transition, ITransitionDriver driver)
        : IPreparedVisualTransition
    {
        private readonly Dictionary<GodotDirectionViewSurface, Item> items = new();
        private bool active, disposed;
        private float progress;

        public void Track(GodotDirectionViewSurface view)
        {
            if (disposed) return;
            if (view.PublishedView?.StageId != stageId) { Remove(view); return; }
            if (items.ContainsKey(view)) return;
            var scratch = owner.nodes.CreateTextureRect("PreparedBackdropDriver");
            scratch.Size = view.TransitionBlendOutput.Size;
            Item? item = null;
            try
            {
                var prepared = GodotVisualTransition.Prepare(driver, new VisualTransitionContext(scratch, transition),
                    GodotVisualInput.Live(view.TransitionBackgroundViewport, new Rect2(Vector2.Zero, view.TransitionBackgroundViewport.Size)),
                    GodotVisualInput.Live(view.BackgroundViewport, new Rect2(Vector2.Zero, view.BackgroundViewport.Size)));
                item = new Item(view, scratch, prepared, transition.Kind == DirectionTransitionId.Fade, owner.demand);
                item.ApplyProgress(progress);
                if (active) item.Activate();
                items.Add(view, item);
            }
            catch { if (item is not null) item.Dispose(); else scratch.Free(); throw; }
        }

        public void Remove(GodotDirectionViewSurface view)
        { if (items.Remove(view, out var item)) item.Dispose(); }
        public void Activate()
        {
            if (disposed) return;
            active = true;
            owner.active[stageId] = this;
            // A camera may have been prepared after this background component.
            foreach (var view in owner.surfaces.RenderViews) Track(view);
            foreach (var item in items.Values) item.Activate();
        }
        public void ApplyProgress(float value)
        { if (disposed) return; progress = value; foreach (var item in items.Values) item.ApplyProgress(value); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (owner.active.GetValueOrDefault(stageId) == this) owner.active.Remove(stageId);
            owner.prepared.Remove(this);
            var failures = new List<Exception>();
            foreach (var item in items.Values)
                try { item.Dispose(); } catch (Exception error) { failures.Add(error); }
            items.Clear();
            if (failures.Count > 0) throw new AggregateException("Background view inputs cleanup failed.", failures);
        }

        internal sealed class Item(GodotDirectionViewSurface view, TextureRect scratch,
            IPreparedVisualTransition prepared, bool fade, GodotViewRenderDemand demand) : IDisposable
        {
            private bool active, disposed;
            private CanvasItem Output => fade ? view.TransitionBackgroundOutput : view.TransitionBlendOutput;
            private Material? installed;
            private IDisposable? backdropDemand;
            public void Activate()
            {
                if (disposed) return;
                backdropDemand ??= demand.AcquireBackdropTransition(view);
                active = true;
                view.BackgroundOutput.Visible = fade;
                view.TransitionBackgroundOutput.Visible = fade;
                view.TransitionBlendOutput.Visible = !fade;
                Copy();
            }
            private void Copy()
            {
                if (!active || !GodotObject.IsInstanceValid(Output)) return;
                Output.Material = installed = scratch.Material;
                Output.Modulate = scratch.Modulate;
            }
            public void ApplyProgress(float progress) { if (disposed) return; prepared.ApplyProgress(progress); Copy(); }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { prepared.Dispose(); }
                finally
                {
                    if (active && GodotObject.IsInstanceValid(Output) && ReferenceEquals(Output.Material, installed))
                    {
                        Output.Material = GodotObject.IsInstanceValid(view.RasterMaterial) ? view.RasterMaterial : null;
                        view.BackgroundOutput.Visible = true;
                        view.TransitionBackgroundOutput.Visible = false;
                        view.TransitionBlendOutput.Visible = false;
                    }
                    try { scratch.Free(); }
                    finally { backdropDemand?.Dispose(); backdropDemand = null; }
                }
            }
        }
    }

}
