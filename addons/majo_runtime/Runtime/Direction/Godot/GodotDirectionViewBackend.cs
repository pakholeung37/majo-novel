#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionViewBackend : IDirectionViewBackend
{
    private readonly GodotDirectionRenderHost host;
    private readonly IDirectionTextureLoader textures;

    public GodotDirectionViewBackend(
        GodotDirectionRenderHost host,
        IDirectionTextureLoader textures)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
    }

    public async Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        ViewId viewId,
        ViewState? before,
        ViewState? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (target is null)
        {
            throw new DirectionReferenceException(
                $"Direction View '{viewId}' has no target state.");
        }
        host.GetOrCreateView(viewId);

        Texture2D targetTexture = target.Source switch
        {
            StageViewSource { StageId: var stageId } => host.GetStageTexture(stageId),
            CgViewSource cg => await textures.LoadAsync(
                cg.Resource,
                cancellationToken),
            _ => throw new DirectionReferenceException(
                $"Unsupported View source '{target.Source.GetType().Name}'."),
        };

        var transition = GodotDirectionRendering.ResolveTransition(
            plan,
            path => path.StartsWith($"view/{viewId.Value}/", StringComparison.Ordinal));
        ImageTexture? capture = null;
        if (transition.DurationSeconds > 0 &&
            transition.Kind is DirectionTransitionKind.Fade or DirectionTransitionKind.Dissolve)
        {
            capture = host.CaptureView(viewId);
        }

        return new PreparedView(
            host,
            viewId,
            before ?? target,
            target,
            targetTexture,
            capture,
            transition);
    }

    private sealed class PreparedView : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly ViewId viewId;
        private readonly ViewState before;
        private readonly ViewState target;
        private readonly Texture2D targetTexture;
        private readonly ImageTexture? capture;
        private readonly DirectionTransitionSpec transition;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Tween? tween;
        private bool committed;
        private bool disposed;

        public PreparedView(
            GodotDirectionRenderHost host,
            ViewId viewId,
            ViewState before,
            ViewState target,
            Texture2D targetTexture,
            ImageTexture? capture,
            DirectionTransitionSpec transition)
        {
            this.host = host;
            this.viewId = viewId;
            this.before = before;
            this.target = target;
            this.targetTexture = targetTexture;
            this.capture = capture;
            this.transition = transition;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed) throw new InvalidOperationException("View already committed.");
            committed = true;
            host.ClearViewTransition();

            if (capture is not null)
            {
                host.TransitionSource.Texture = capture;
                host.TransitionSource.Modulate = Colors.White;
                host.TransitionSource.Visible = true;
            }

            host.PublishView(viewId, target, targetTexture);
            if (transition.DurationSeconds <= 0 || transition.Kind == DirectionTransitionKind.Cut)
            {
                Complete();
                return;
            }

            tween = host.CreateTween();
            GodotDirectionRendering.Configure(tween, transition.Ease);
            switch (transition.Kind)
            {
                case DirectionTransitionKind.Move:
                    host.ApplyViewPose(viewId, before.Pose);
                    tween.TweenMethod(
                        Callable.From<float>(progress => host.ApplyViewPose(
                            viewId,
                            GodotDirectionRendering.Interpolate(
                                before.Pose,
                                target.Pose,
                                progress))),
                        0.0f,
                        1.0f,
                        transition.DurationSeconds);
                    break;
                case DirectionTransitionKind.Fade:
                case DirectionTransitionKind.Dissolve:
                    if (capture is null)
                    {
                        Complete();
                        return;
                    }
                    tween.TweenProperty(
                        host.TransitionSource,
                        "modulate:a",
                        0,
                        transition.DurationSeconds);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(transition));
            }
            tween.Finished += OnFinished;
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed) throw new InvalidOperationException("View has not committed.");
            Complete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            if (!committed) settlement.TrySetCanceled();
            if (tween is not null && GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            host.ClearViewTransition();
            capture?.Dispose();
            return ValueTask.CompletedTask;
        }

        private void OnFinished() => Complete();

        private void Complete()
        {
            if (settlement.Task.IsCompleted) return;
            if (tween is not null && GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            host.PublishView(viewId, target, targetTexture);
            host.ClearViewTransition();
            settlement.TrySetResult();
        }
    }
}
