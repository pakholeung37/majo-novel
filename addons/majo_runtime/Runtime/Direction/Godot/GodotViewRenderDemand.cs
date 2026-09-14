#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Majo.Runtime.Direction.Godot;

/// <summary>Godot-thread-only View consumption graph. Resource ownership and submission
/// ordering remain with the render host. Recompute only when demand changes.</summary>
internal sealed class GodotViewRenderDemand
{
    private readonly Dictionary<GodotDirectionViewSurface, Entry> entries = new();
    private readonly HashSet<GodotDirectionViewSurface> screenSources = new();
    private bool screenEnabled;

    internal void Register(GodotDirectionViewSurface view) => entries.Add(view, new Entry(view));

    internal void Remove(GodotDirectionViewSurface view)
    {
        if (!entries.Remove(view, out var entry)) return;
        entry.Registered = false;
        SetRendering(entry, false);
        screenSources.Remove(view);
        foreach (var other in entries.Values) other.Sources.Remove(entry);
        Recompute();
    }

    internal void Clear()
    {
        foreach (var entry in entries.Values)
        {
            entry.Registered = false;
            SetRendering(entry, false);
        }
        entries.Clear();
        screenSources.Clear();
    }

    internal void SetScreen(IEnumerable<GodotDirectionViewSurface> sources, bool enabled)
    {
        var changed = !screenSources.SetEquals(sources);
        if (!changed && screenEnabled == enabled) return;
        if (changed)
        {
            screenSources.Clear();
            screenSources.UnionWith(sources);
        }
        screenEnabled = enabled;
        Recompute();
    }

    internal Request Acquire(GodotDirectionViewSurface view, string reason)
    {
        var entry = entries[view];
        var request = new Request(this, entry, null, reason);
        entry.Requests.Add(request);
        Recompute();
        return request;
    }

    internal Request Depend(GodotDirectionViewSurface consumer, GodotDirectionViewSurface source)
    {
        var entry = entries[source];
        var owner = entries[consumer];
        owner.Sources.TryGetValue(entry, out var count);
        owner.Sources[entry] = count + 1;
        Recompute();
        return new Request(this, entry, owner, "camera transition");
    }

    internal IReadOnlyList<Snapshot> Inspect() => entries.Values.Select(entry => new Snapshot(
        entry.View.Id.Value, entry.Rendering,
        screenEnabled && screenSources.Contains(entry.View),
        entry.Requests.Select(request => request.Reason).ToArray(),
        entry.Sources.Keys.Select(source => source.View.Id.Value).ToArray())).ToArray();

    internal sealed record Snapshot(string View, bool Rendering, bool Screen,
        string[] Requests, string[] Sources);

    private void Release(Request request, Entry entry, Entry? consumer)
    {
        // Tokens belong to registration instances, including when reset reuses main View.
        if (!entry.Registered) return;
        if (consumer is null) entry.Requests.Remove(request);
        else if (consumer.Registered && consumer.Sources.TryGetValue(entry, out var count))
        {
            if (count == 1) consumer.Sources.Remove(entry);
            else consumer.Sources[entry] = count - 1;
        }
        Recompute();
    }

    private void Recompute()
    {
        var needed = new HashSet<Entry>();
        var pending = new Stack<Entry>(entries.Values.Where(entry => entry.Requests.Count > 0 ||
            (screenEnabled && screenSources.Contains(entry.View))));
        while (pending.TryPop(out var entry))
        {
            if (!needed.Add(entry)) continue;
            foreach (var source in entry.Sources.Keys) pending.Push(source);
        }
        foreach (var entry in entries.Values) SetRendering(entry, needed.Contains(entry));
    }

    // The only writer of registered View update modes. All five passes share one demand.
    private static void SetRendering(Entry entry, bool enabled)
    {
        if (entry.Rendering == enabled) return;
        entry.Rendering = enabled;
        var mode = enabled ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        var view = entry.View;
        view.Viewport.RenderTargetUpdateMode = mode;
        view.ContentViewport.RenderTargetUpdateMode = mode;
        view.BackgroundViewport.RenderTargetUpdateMode = mode;
        view.TransitionBackgroundViewport.RenderTargetUpdateMode = mode;
        view.CompositeViewport.RenderTargetUpdateMode = mode;
    }

    internal sealed class Entry(GodotDirectionViewSurface view)
    {
        internal GodotDirectionViewSurface View { get; } = view;
        internal bool Registered { get; set; } = true;
        internal bool Rendering { get; set; }
        internal HashSet<Request> Requests { get; } = new();
        internal Dictionary<Entry, int> Sources { get; } = new();
    }

    internal sealed class Request(GodotViewRenderDemand graph, Entry entry, Entry? consumer, string reason) : IDisposable
    {
        private bool disposed;
        internal string Reason { get; } = reason;
        internal bool IsValid => !disposed && entry.Registered && (consumer is null || consumer.Registered);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            graph.Release(this, entry, consumer);
        }
    }
}
