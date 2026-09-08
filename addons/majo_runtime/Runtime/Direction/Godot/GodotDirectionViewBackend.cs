#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Timing;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionViewBackend : IDirectionViewBackend
{
    private readonly Dictionary<ViewId, PreparedView> active = new();
    private readonly GodotDirectionRenderHost host;
    private readonly ITransitionDriverRegistry drivers;
    private readonly IEasingRegistry easings;

    public GodotDirectionViewBackend(
        GodotDirectionRenderHost host,
        ITransitionDriverRegistry? drivers = null,
        IEasingRegistry? easings = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.drivers = drivers ?? new TransitionDriverRegistry();
        this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
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
            var transition = GodotDirectionRendering.ResolveTransition(
                plan,
                path => path.StartsWith($"view/{viewId.Value}/", StringComparison.Ordinal));
            GodotDirectionRendering.ValidateTransition(transition, TransitionDomains.View, drivers, easings);
            ImageTexture? capture = null;
            if (transition.DurationSeconds > 0 &&
                transition.Kind != DirectionTransitionId.Cut &&
                transition.Kind != DirectionTransitionId.Move)
            {
                capture = await host.CaptureViewAsync(viewId, cancellationToken,
                    active.TryGetValue(viewId, out var previous) && !previous.IsMotion);
            }

            var source = before ?? target;
            if (before is not null)
                source = source with
                {
                    Pose = host.GetCurrentViewPose(viewId),
                };
            var prepared = new PreparedView(
                this,
                plan,
                host,
                viewId,
                before,
                source,
                beforeTexture,
                target,
                targetTexture,
                capture,
                transition,
                drivers,
                easings);
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

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            if (!settlement.Task.IsCompleted) settlement.TrySetCanceled();
            if (committed)
            {
                if (host.ReleaseViewEffectsAsync is { } release) await release(viewId);
                host.ReleasePreparedView(viewId);
            }
        }
    }

    private sealed class PreparedView : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly ViewId viewId;
        private readonly ViewState? restoreState;
        private readonly GodotDirectionViewBackend backend;
        private readonly DirectionPlan plan;
        private ViewState before;
        private readonly Texture2D? restoreTexture;
        private ViewState target;
        private readonly Texture2D targetTexture;
        private readonly ImageTexture? capture;
        public bool IsMotion => transition.Kind == DirectionTransitionId.Move;
        private readonly DirectionTransitionSpec transition;
        private readonly ITransitionDriverRegistry drivers;
        private readonly IEasingRegistry easings;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private GodotProgressTween? clock;
        private IPreparedVisualTransition? preparedTransition;
        private bool committed;
        private bool rolledBack;
        private bool disposed;
        private long generation;

        public PreparedView(
            GodotDirectionViewBackend backend,
            DirectionPlan plan,
            GodotDirectionRenderHost host,
            ViewId viewId,
            ViewState? restoreState,
            ViewState before,
            Texture2D? restoreTexture,
            ViewState target,
            Texture2D targetTexture,
            ImageTexture? capture,
            DirectionTransitionSpec transition,
            ITransitionDriverRegistry drivers,
            IEasingRegistry? easings = null)
        {
            this.host = host;
            this.viewId = viewId;
            this.backend = backend;
            this.plan = plan;
            this.restoreState = restoreState;
            this.before = before;
            this.restoreTexture = restoreTexture;
            this.target = target;
            this.targetTexture = targetTexture;
            this.capture = capture;
            this.transition = transition;
            this.drivers = drivers ?? new TransitionDriverRegistry();
            this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("View is no longer preparable.");
            committed = true;
            if (backend.active.TryGetValue(viewId, out var previous))
            {
                // A visual mix is completed before replacement; motion is sampled in place.
                if (previous.transition.Kind == DirectionTransitionId.Move) previous.Supersede();
                else previous.Complete();
            }
            before = before with { Pose = host.GetCurrentViewPose(viewId) };
            var intent = plan.ViewIntents.SingleOrDefault(patch => patch.ViewId == viewId);
            if (intent is not null)
            {
                target = target with
                {
                    Pose = before.Pose with
                    {
                        Center = intent.Center.HasValue ? intent.Center.Value : before.Pose.Center,
                        Zoom = intent.Zoom.HasValue ? intent.Zoom.Value : before.Pose.Zoom,
                        RotationDegrees = intent.RotationDegrees.HasValue ? intent.RotationDegrees.Value : before.Pose.RotationDegrees,
                    }
                };
                plan.CommitViews[viewId] = target;
            }
            backend.active[viewId] = this;
            generation = host.BeginViewGeneration(viewId);
            host.ClearViewTransition(viewId);
            var transitionSource = host.GetViewTransitionSource(viewId);

            if (capture is not null)
            {
                transitionSource.Texture = capture;
                transitionSource.Modulate = Colors.White;
                transitionSource.Visible = true;
            }

            host.PublishView(
                viewId,
                target,
                targetTexture);
            if (transition.DurationSeconds <= 0 || transition.Kind == DirectionTransitionId.Cut)
            {
                Complete();
                return;
            }

            if (transition.Kind == DirectionTransitionId.Move)
            {
                host.ApplyViewPose(viewId, before.Pose);
                clock = host.Animate(transition, easings.GetRequired(transition.Easing), p =>
                {
                    if (host.IsCurrentViewGeneration(viewId, generation))
                        host.ApplyViewPose(viewId, GodotDirectionRendering.Interpolate(before.Pose, target.Pose, p));
                }, OnFinished, exception => settlement.TrySetException(exception));
            }
            else
            {
                if (capture is null) { Complete(); return; }
                var surface = host.GetOrCreateView(viewId);
                var context = new VisualTransitionContext(transitionSource, transition, capture,
                    surface.ContentViewport.GetTexture(), true, true,
                    () => host.IsCurrentViewGeneration(viewId, generation));
                preparedTransition = drivers.GetRequired(transition.Kind).Prepare(context);
                if (transition.Kind != DirectionTransitionId.Fade) surface.Output.Visible = false;
                clock = host.Animate(transition, easings.GetRequired(transition.Easing), p =>
                {
                    if (host.IsCurrentViewGeneration(viewId, generation)) preparedTransition.ApplyProgress(p);
                }, OnFinished, exception => settlement.TrySetException(exception));
            }
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
            clock?.Dispose();
            clock = null;
            preparedTransition?.Dispose();
            preparedTransition = null;
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
            clock?.Dispose();
            clock = null;
            preparedTransition?.Dispose();
            preparedTransition = null;
            if (host.IsCurrentViewGeneration(viewId, generation))
                host.ClearViewTransition(viewId);
            Retire();
            capture?.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Supersede()
        {
            clock?.Dispose();
            clock = null;
            preparedTransition?.Dispose();
            preparedTransition = null;
            Retire();
            settlement.TrySetResult();
        }

        private void Retire()
        {
            if (backend.active.TryGetValue(viewId, out var current) && ReferenceEquals(current, this))
                backend.active.Remove(viewId);
        }

        private void OnFinished() => Complete();

        private void Complete()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            clock?.Dispose();
            clock = null;
            if (host.IsCurrentViewGeneration(viewId, generation))
            {
                preparedTransition?.ApplyProgress(1.0f);
                host.PublishView(viewId, target, targetTexture);
                host.ClearViewTransition(viewId);
            }
            preparedTransition?.Dispose();
            preparedTransition = null;
            Retire();
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
