#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Direction.Godot;

public enum VisualInputUpdate { Static, Live, Frozen }

/// <summary>A render input with an explicit update policy and lifetime. Use on the Godot thread.
/// The release callback owns the content/render lease; without one the enclosing presentation
/// must keep the producer alive until this handle is disposed. Bounds use producer coordinates.</summary>
public sealed class GodotVisualInput : IDisposable
{
    private readonly SubViewport? viewport;
    private Action? release;
    private bool disposed;
    private GodotVisualInput(Texture2D texture, Rect2 bounds, bool premultiplied,
        VisualInputUpdate update, SubViewport? viewport, Action? release)
    {
        Texture = texture; Bounds = bounds; IsPremultiplied = premultiplied;
        Update = update; this.viewport = viewport; this.release = release;
    }

    public Texture2D Texture { get; }
    public Rect2 Bounds { get; }
    public bool IsPremultiplied { get; }
    public VisualInputUpdate Update { get; }

    public static GodotVisualInput Live(SubViewport viewport, Rect2 bounds, Action? release = null) =>
        new(viewport.GetTexture(), bounds, true, VisualInputUpdate.Live, viewport, release);

    public static GodotVisualInput Static(Texture2D texture, Rect2 bounds, bool premultiplied = false,
        Action? release = null) => new(texture, bounds, premultiplied, VisualInputUpdate.Static, null, release);

    public static GodotVisualInput Frozen(Texture2D texture, Rect2 bounds, bool premultiplied = true,
        Action? release = null) => new(texture, bounds, premultiplied, VisualInputUpdate.Frozen, null, release);

    public Task ReadyAsync(CancellationToken token = default) => ReadyAsync(null, token);

    public async Task ReadyAsync(SceneTree? headlessTree, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (viewport is not null) await WaitForFrameAsync(headlessTree, token);
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    /// <summary>Explicit frame retention, e.g. restore coverage. Ordinary transitions keep Live inputs.</summary>
    public GodotVisualInput Freeze()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Texture2D frozen;
        if (DisplayServer.GetName() == "headless") frozen = new PlaceholderTexture2D { Size = Bounds.Size };
        else
        {
            using var image = Texture.GetImage();
            if (image is null || image.IsEmpty()) throw new InvalidOperationException("Visual input has no rendered frame.");
            frozen = ImageTexture.CreateFromImage(image);
        }
        return new GodotVisualInput(frozen, Bounds, IsPremultiplied, VisualInputUpdate.Frozen, null, frozen.Dispose);
    }

    public static Task WaitForFrameAsync(CancellationToken token = default) => WaitForFrameAsync(null, token);

    public static async Task WaitForFrameAsync(SceneTree? headlessTree, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (DisplayServer.GetName() == "headless")
        {
            if (headlessTree is null) return;
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Process() => frame.TrySetResult();
            headlessTree.ProcessFrame += Process;
            using var cancelled = token.Register(() => frame.TrySetCanceled(token));
            try { await frame.Task; }
            finally { headlessTree.ProcessFrame -= Process; }
            token.ThrowIfCancellationRequested();
            return;
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var began = false;
        void Before() => began = true;
        void After() { if (began) ready.TrySetResult(); }
        RenderingServer.FramePreDraw += Before;
        RenderingServer.FramePostDraw += After;
        using var cancellation = token.Register(() => ready.TrySetCanceled(token));
        try { await ready.Task; }
        finally { RenderingServer.FramePreDraw -= Before; RenderingServer.FramePostDraw -= After; }
        token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var callback = release; release = null;
        callback?.Invoke();
    }
}

/// <summary>Common actor/background/camera driver bridge. Inputs must be ready before publication.
/// Takes ownership of input handles, including on preparation failure. The driver releases its
/// texture consumers before producer leases are released.</summary>
public sealed class GodotVisualTransition : IPreparedVisualTransition
{
    private readonly IPreparedVisualTransition effect;
    private readonly GodotVisualInput? source, target;
    private bool disposed;
    private GodotVisualTransition(IPreparedVisualTransition effect, GodotVisualInput? source, GodotVisualInput? target)
    { this.effect = effect; this.source = source; this.target = target; }

    public static GodotVisualTransition Prepare(ITransitionDriver driver, VisualTransitionContext context,
        GodotVisualInput? source, GodotVisualInput? target)
    {
        try
        {
            var effect = driver.Prepare(context with
            {
                SourceTexture = source?.Texture,
                TargetTexture = target?.Texture,
                SourceIsPremultiplied = source?.IsPremultiplied ?? false,
                TargetIsPremultiplied = target?.IsPremultiplied ?? false,
            });
            return new GodotVisualTransition(effect, source, target);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            try { source?.Dispose(); } catch (Exception failure) { failures.Add(failure); }
            try { target?.Dispose(); } catch (Exception failure) { failures.Add(failure); }
            if (failures.Count > 1) throw new AggregateException("Visual input preparation cleanup failed.", failures);
            throw;
        }
    }

    public void ApplyProgress(float progress) { if (!disposed) effect.ApplyProgress(progress); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var failures = new List<Exception>();
        try { effect.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { source?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { target?.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Visual transition cleanup failed.", failures);
    }
}
