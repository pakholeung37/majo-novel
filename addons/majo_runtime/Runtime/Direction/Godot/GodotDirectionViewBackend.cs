#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionViewBackend : IDirectionViewBackend
{
    private readonly GodotDirectionRenderHost host;

    public GodotDirectionViewBackend(GodotDirectionRenderHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
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
            if (before is null)
                throw new DirectionReferenceException(
                    $"Direction View '{viewId}' has neither source nor target state.");
            return new PreparedViewDeletion(host, viewId);
        }
        var createdView = !host.HasView(viewId);
        host.GetOrCreateView(viewId);
        try
        {
            Texture2D? beforeTexture = before is null
                ? null
                : host.GetStageTexture(before.StageId);
            Texture2D targetTexture = host.GetStageTexture(target.StageId);

            await host.WaitForRenderFenceAsync(cancellationToken);
            var sequence = plan.ViewSequences?.FirstOrDefault(
                candidate => candidate.ViewId == viewId);
            var transition = sequence?.Transition ?? GodotDirectionRendering.ResolveTransition(
                plan,
                path => path.StartsWith($"view/{viewId.Value}/", StringComparison.Ordinal));
            ImageTexture? capture = null;
            if (transition.DurationSeconds > 0 &&
                transition.Kind is DirectionTransitionKind.Fade or DirectionTransitionKind.Dissolve)
            {
                capture = await host.CaptureViewAsync(viewId, cancellationToken);
            }

            var source = sequence?.StartState ?? before ?? target;
            if (before is not null)
                source = source with
                {
                    Pose = sequence?.StartState.Pose ?? host.GetCurrentViewPose(viewId),
                };
            var prepared = new PreparedView(
                host,
                viewId,
                before,
                source,
                beforeTexture,
                target,
                targetTexture,
                capture,
                transition,
                sequence is not null);
            return createdView ? new PreparedViewLifecycle(host, viewId, prepared) : prepared;
        }
        catch
        {
            if (createdView) host.ReleasePreparedView(viewId);
            throw;
        }
    }

    private sealed class PreparedViewDeletion : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly ViewId viewId;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool committed;
        private bool disposed;

        public PreparedViewDeletion(GodotDirectionRenderHost host, ViewId viewId)
        {
            this.host = host;
            this.viewId = viewId;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || disposed)
                throw new InvalidOperationException("View deletion is no longer preparable.");
            committed = true;
            settlement.TrySetResult();
        }

        public void Rollback(DirectionCommitToken token) => committed = false;

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed || disposed)
                throw new InvalidOperationException("View deletion cannot finish in its current state.");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            if (!settlement.Task.IsCompleted) settlement.TrySetCanceled();
            if (committed) host.ReleasePreparedView(viewId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreparedView : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly ViewId viewId;
        private readonly ViewState? restoreState;
        private readonly ViewState before;
        private readonly Texture2D? restoreTexture;
        private readonly ViewState target;
        private readonly Texture2D targetTexture;
        private readonly ImageTexture? capture;
        private readonly DirectionTransitionSpec transition;
        private readonly bool publishTransitionStart;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Tween? tween;
        private bool committed;
        private bool rolledBack;
        private bool disposed;
        private long generation;

        public PreparedView(
            GodotDirectionRenderHost host,
            ViewId viewId,
            ViewState? restoreState,
            ViewState before,
            Texture2D? restoreTexture,
            ViewState target,
            Texture2D targetTexture,
            ImageTexture? capture,
            DirectionTransitionSpec transition,
            bool publishTransitionStart)
        {
            this.host = host;
            this.viewId = viewId;
            this.restoreState = restoreState;
            this.before = before;
            this.restoreTexture = restoreTexture;
            this.target = target;
            this.targetTexture = targetTexture;
            this.capture = capture;
            this.transition = transition;
            this.publishTransitionStart = publishTransitionStart;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("View is no longer preparable.");
            committed = true;
            generation = host.BeginViewGeneration(viewId);
            host.ClearViewTransition(viewId);
            var transitionSource = host.GetViewTransitionSource(viewId);

            if (capture is not null)
            {
                transitionSource.Texture = capture;
                transitionSource.Modulate = Colors.White;
                transitionSource.Visible = true;
            }

            // A sequenced pose starts from the cut baseline captured in
            // `before`; the normal case uses the actual current pose there.
            host.PublishView(
                viewId,
                publishTransitionStart ? before : target,
                targetTexture);
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
                        Callable.From<float>(progress =>
                        {
                            if (!host.IsCurrentViewGeneration(viewId, generation)) return;
                            host.ApplyViewPose(
                                viewId,
                                GodotDirectionRendering.Interpolate(
                                    before.Pose,
                                    target.Pose,
                                    progress));
                        }),
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
                    tween.TweenMethod(Callable.From<float>(progress =>
                    {
                        if (host.IsCurrentViewGeneration(viewId, generation))
                            transitionSource.Modulate = new Color(
                                transitionSource.Modulate, 1 - progress);
                    }), 0.0f, 1.0f, transition.DurationSeconds);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported transition kind: {transition.Kind}.");
            }
            tween.Finished += OnFinished;
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed || rolledBack || disposed)
                throw new InvalidOperationException("View cannot finish in its current state.");
            Complete();
            return ValueTask.CompletedTask;
        }

        public void Rollback(DirectionCommitToken token)
        {
            if (rolledBack || disposed) return;
            rolledBack = true;
            generation = host.BeginViewGeneration(viewId);
            if (tween is not null && GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            host.ClearViewTransition(viewId);
            if (restoreState is not null && restoreTexture is not null)
                host.PublishView(viewId, restoreState, restoreTexture);
            settlement.TrySetCanceled();
        }

        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            if (!settlement.Task.IsCompleted) settlement.TrySetCanceled();
            if (tween is not null && GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            if (host.IsCurrentViewGeneration(viewId, generation))
                host.ClearViewTransition(viewId);
            capture?.Dispose();
            return ValueTask.CompletedTask;
        }

        private void OnFinished() => Complete();

        private void Complete()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            if (tween is not null && GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            if (host.IsCurrentViewGeneration(viewId, generation))
            {
                host.PublishView(viewId, target, targetTexture);
                host.ClearViewTransition(viewId);
            }
            settlement.TrySetResult();
        }
    }

    private sealed class PreparedViewLifecycle : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly ViewId viewId;
        private readonly IPreparedDirectionComponent component;
        private bool committed;
        private bool commitStarted;
        private bool disposed;

        public PreparedViewLifecycle(GodotDirectionRenderHost host, ViewId viewId, IPreparedDirectionComponent component)
        {
            this.host = host;
            this.viewId = viewId;
            this.component = component;
        }

        public Task Settlement => component.Settlement;
        public void Commit(DirectionCommitToken token)
        {
            if (committed || disposed)
                throw new InvalidOperationException("View lifecycle is no longer preparable.");
            commitStarted = true;
            component.Commit(token);
            committed = true;
        }
        public void Rollback(DirectionCommitToken token)
        {
            if (!commitStarted) return;
            try { component.Rollback(token); }
            finally { committed = false; }
        }
        public ValueTask FinishAsync(CancellationToken cancellationToken) => component.FinishAsync(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await component.DisposeAsync();
            if (!committed) host.ReleasePreparedView(viewId);
        }
    }
}
