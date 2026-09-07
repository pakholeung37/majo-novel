#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Owns two actor-local inputs. Neither input captures the scene behind the actor.</summary>
internal sealed class GodotActorAppearanceTransition : IPreparedVisualTransition
{
    private readonly SubViewport source;
    private readonly SubViewport target;
    private readonly Sprite2D overlay;
    private readonly Action<bool> showOriginal;
    private readonly Action<Sprite2D?> publishOverlay;
    private IPreparedVisualTransition? effect;
    private bool disposed;

    private GodotActorAppearanceTransition(Node2D owner, CanvasItem sourceContent,
        CanvasItem targetContent, Rect2 bounds, float opacity, Action<bool> showOriginal,
        Action<Sprite2D?> publishOverlay)
    {
        this.showOriginal = showOriginal;
        this.publishOverlay = publishOverlay;
        source = CreateInput("AppearanceSource", sourceContent, bounds);
        target = CreateInput("AppearanceTarget", targetContent, bounds);
        overlay = new Sprite2D
        {
            Name = $"{owner.Name}_DissolveOverlay",
            Texture = target.GetTexture(),
            Centered = false,
            Position = bounds.Position,
            Visible = false,
            Modulate = new Color(1, 1, 1, opacity),
        };
        owner.AddChild(source);
        owner.AddChild(target);
        owner.AddChild(overlay);
    }

    public static async Task<IPreparedVisualTransition> PrepareAsync(Node2D owner,
        CanvasItem sourceContent, CanvasItem targetContent, Rect2 bounds, float opacity,
        Action<bool> showOriginal, Action<Sprite2D?> publishOverlay,
        ITransitionDriver driver, AnimationSpec animation, CancellationToken cancellationToken)
    {
        var result = new GodotActorAppearanceTransition(owner, sourceContent, targetContent,
            bounds, opacity, showOriginal, publishOverlay);
        try
        {
            var context = new VisualTransitionContext(result.overlay,
                new DirectionTransitionSpec(driver.Id, animation.DurationSeconds, animation.Easing),
                result.source.GetTexture(), result.target.GetTexture(), true, true);
            result.effect = driver.Prepare(context);
            // Keep the stable actor visible until both offscreen inputs have rendered.
            for (var i = 0; i < 2; i++)
            {
                await NextFrameAsync(owner.GetTree()).WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            result.source.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            result.target.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            result.effect.ApplyProgress(0);
            showOriginal(false);
            result.overlay.Visible = true;
            publishOverlay(result.overlay);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static async Task NextFrameAsync(SceneTree tree)
    {
        if (DisplayServer.GetName() == "headless")
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        else
            await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    private static SubViewport CreateInput(string name, CanvasItem content, Rect2 bounds)
    {
        var viewport = new SubViewport
        {
            Name = name,
            Size = (Vector2I)bounds.Size.Ceil(),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var offset = new Node2D { Position = -bounds.Position };
        viewport.AddChild(offset);
        offset.AddChild(content);
        content.Visible = true;
        return viewport;
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
                if (GodotObject.IsInstanceValid(source)) source.Free();
                if (GodotObject.IsInstanceValid(target)) target.Free();
            }
        }
    }

    public static OperationDefinition CreateOperation(Node owner, AnimationSpec animation,
        EasingDefinition easing, Func<CancellationToken, Task<IPreparedVisualTransition>> prepare,
        Action commit)
    {
        IPreparedVisualTransition? prepared = null;
        OperationDefinition? progress = null;
        var complete = false;
        void Cleanup() { prepared?.Dispose(); prepared = null; }
        void Complete()
        {
            if (complete) return;
            complete = true;
            Cleanup();
            commit();
        }
        return new OperationDefinition(
            async context =>
            {
                try
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (animation.DurationSeconds == 0) { Complete(); return; }
                    prepared = await prepare(context.CancellationToken);
                    progress = GodotTweenOperation.CreateProgressOperation(owner, animation.Timing,
                        easing, p => prepared?.ApplyProgress(p));
                    await progress.ExecuteAsync(context);
                    Complete();
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                { throw; }
                catch { Cleanup(); throw; }
            },
            onFinish: async token =>
            {
                try { if (progress is not null) await progress.FinishAsync(token); }
                finally { Complete(); }
            },
            onCancel: async token =>
            {
                try { if (progress is not null) await progress.CancelAsync(token); }
                finally { Complete(); }
            });
    }
}
