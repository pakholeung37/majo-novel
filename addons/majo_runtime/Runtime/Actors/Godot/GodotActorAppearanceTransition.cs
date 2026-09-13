#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Owns two actor-local inputs. Neither input captures the scene behind the actor.</summary>
internal sealed class GodotActorAppearanceTransition : IPreparedVisualTransition
{
    private readonly GodotActorCapturePool.Capture capture;
    private readonly SubViewport source;
    private readonly SubViewport target;
    private readonly Sprite2D overlay;
    private readonly Action<bool> showOriginal;
    private readonly Action<Sprite2D?> publishOverlay;
    private GodotVisualTransition? effect;
    private bool disposed;
    private GodotVisualInput? sourceInput, targetInput;

    private GodotActorAppearanceTransition(Node2D owner, CanvasItem sourceContent,
        CanvasItem targetContent, Rect2 bounds, float opacity, Action<bool> showOriginal,
        Action<Sprite2D?> publishOverlay)
    {
        this.showOriginal = showOriginal;
        this.publishOverlay = publishOverlay;
        capture = GodotActorCapturePool.For(owner).Acquire(sourceContent, targetContent, bounds);
        source = capture.Source;
        target = capture.Target;
        overlay = new Sprite2D
        {
            Name = $"{owner.Name}_DissolveOverlay",
            Texture = target.GetTexture(),
            Centered = false,
            Position = bounds.Position,
            Scale = bounds.Size.Ceil() / (Vector2)target.Size,
            Visible = false,
            VisibilityLayer = owner.VisibilityLayer,
            Modulate = new Color(1, 1, 1, opacity),
        };
        owner.AddChild(overlay);
    }

    public static async Task<IPreparedVisualTransition> PrepareAsync(Node2D owner,
        CanvasItem sourceContent, CanvasItem targetContent, Rect2 bounds, float opacity,
        Action<bool> showOriginal, Action<Sprite2D?> publishOverlay,
        ITransitionDriver driver, AnimationSpec animation, CancellationToken cancellationToken, bool freeze = false)
    {
        var result = new GodotActorAppearanceTransition(owner, sourceContent, targetContent,
            bounds, opacity, showOriginal, publishOverlay);
        try
        {
            result.sourceInput = GodotVisualInput.Live(result.source, bounds);
            result.targetInput = GodotVisualInput.Live(result.target, bounds);
            // Observe full submissions, including upstream model/mask dependencies.
            await result.sourceInput.ReadyAsync(owner.GetTree(), cancellationToken);
            await result.targetInput.ReadyAsync(owner.GetTree(), cancellationToken);
            if (freeze)
            {
                var frozenSource = result.sourceInput.Freeze();
                result.sourceInput.Dispose(); result.sourceInput = frozenSource;
                var frozenTarget = result.targetInput.Freeze();
                result.targetInput.Dispose(); result.targetInput = frozenTarget;
                result.source.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                result.target.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
            var context = new VisualTransitionContext(result.overlay,
                new DirectionTransitionSpec(driver.Id, animation.DurationSeconds, animation.Easing));
            result.effect = GodotVisualTransition.Prepare(driver, context, result.sourceInput, result.targetInput);
            result.effect.ApplyProgress(0);
            showOriginal(false);
            result.overlay.Visible = true;
            publishOverlay(result.overlay);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static async Task<IPreparedActorAppearance> PrepareForCommitAsync(Node2D owner,
        CanvasItem sourceContent, CanvasItem targetContent, Rect2 bounds,
        Action<bool> showOriginal, Action<Sprite2D?> publishOverlay, Action publishTarget, Action finish,
        string resolvedAppearance,
        ITransitionDriver driver, AnimationSpec animation, CancellationToken cancellationToken)
    {
        var staging = new Node2D
        {
            Name = "PreparedAppearance",
            Visible = false,
            VisibilityLayer = owner.VisibilityLayer,
        };
        owner.GetParent().AddChild(staging);
        try
        {
            var effect = (GodotActorAppearanceTransition)await PrepareAsync(staging,
                sourceContent, targetContent, bounds, 1, _ => { }, _ => { }, driver, animation, cancellationToken);
            return new PreparedAppearance(owner, staging, effect, showOriginal, publishOverlay,
                publishTarget, finish, resolvedAppearance);
        }
        catch { staging.QueueFree(); throw; }
    }

    private sealed class PreparedAppearance : IPreparedActorAppearance
    {
        private readonly Node2D owner;
        private readonly Node2D staging;
        private readonly GodotActorAppearanceTransition effect;
        private readonly Action<bool> showOriginal;
        private readonly Action<Sprite2D?> publishOverlay;
        private readonly Action publishTarget;
        private readonly Action finish;
        private bool committed;
        private bool disposed;

        public PreparedAppearance(Node2D owner, Node2D staging, GodotActorAppearanceTransition effect,
            Action<bool> showOriginal, Action<Sprite2D?> publishOverlay, Action publishTarget, Action finish,
            string resolvedAppearance)
        {
            this.owner = owner; this.staging = staging; this.effect = effect;
            this.showOriginal = showOriginal; this.publishOverlay = publishOverlay;
            this.publishTarget = publishTarget; this.finish = finish;
            ResolvedAppearance = resolvedAppearance;
        }

        public string ResolvedAppearance { get; }

        public void Commit()
        {
            if (disposed || committed) throw new InvalidOperationException("Appearance is no longer preparable.");
            // Leased inputs stay in the pool; only the visible overlay follows the actor.
            effect.overlay.Reparent(owner, false);
            effect.overlay.VisibilityLayer = owner.VisibilityLayer;
            committed = true;
            publishTarget();
            showOriginal(false);
            publishOverlay(effect.overlay);
        }

        public void ApplyProgress(float progress) { if (!disposed) effect.ApplyProgress(progress); }
        public void Finish()
        {
            if (disposed) return;
            Dispose();
            finish();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            effect.Dispose();
            if (committed) { showOriginal(true); publishOverlay(null); }
            if (GodotObject.IsInstanceValid(staging)) staging.QueueFree();
        }
    }

    public void ApplyProgress(float progress) => effect?.ApplyProgress(progress);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { effect?.Dispose(); }
        finally
        {
            effect = null;
            try { showOriginal(true); }
            finally
            {
                publishOverlay(null);
                if (GodotObject.IsInstanceValid(overlay)) overlay.Free();
                try { sourceInput?.Dispose(); }
                finally { try { targetInput?.Dispose(); } finally { capture.Release(); } }
            }
        }
    }

}

public sealed class GodotActorAppearanceTransitionService : IActorAppearanceTransitionService
{
    public Task<IPreparedActorAppearance> PrepareForCommitAsync(
        Node2D owner, CanvasItem sourceContent, CanvasItem targetContent, Rect2 bounds,
        Action<bool> showOriginal, Action<Sprite2D?> publishOverlay, Action publishTarget,
        Action finish, string resolvedAppearance, ITransitionDriver driver,
        AnimationSpec animation, CancellationToken cancellationToken = default) =>
        GodotActorAppearanceTransition.PrepareForCommitAsync(owner, sourceContent, targetContent,
            bounds, showOriginal, publishOverlay, publishTarget, finish, resolvedAppearance,
            driver, animation, cancellationToken);

    public Task<IPreparedVisualTransition> PrepareSnapshotAsync(
        Node2D owner, CanvasItem sourceContent, CanvasItem targetContent, Rect2 bounds,
        float opacity, Action<bool> showOriginal, Action<Sprite2D?> publishOverlay,
        ITransitionDriver driver, AnimationSpec animation,
        CancellationToken cancellationToken = default) =>
        GodotActorAppearanceTransition.PrepareAsync(owner, sourceContent, targetContent, bounds,
            opacity, showOriginal, publishOverlay, driver, animation, cancellationToken, freeze: true);
}
