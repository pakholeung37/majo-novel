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
        CancellationToken cancellationToken,
        IReadOnlyList<DirectionViewTransitionSequence>? viewSequences = null)
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
            var sequence = (viewSequences ?? Array.Empty<DirectionViewTransitionSequence>()).FirstOrDefault(
                candidate => candidate.ViewId == viewId);
            var transition = sequence?.Transition ?? GodotDirectionRendering.ResolveTransition(
                plan,
                path => path.StartsWith($"view/{viewId.Value}/", StringComparison.Ordinal));
            GodotDirectionRendering.ValidateTransition(transition, TransitionDomains.View, drivers, easings);
            ImageTexture? capture = null;
            if (transition.DurationSeconds > 0 &&
                transition.Kind != DirectionTransitionId.Cut &&
                transition.Kind != DirectionTransitionId.Move)
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
                sequence is not null,
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
            GodotDirectionRenderHost host,
            ViewId viewId,
            ViewState? restoreState,
            ViewState before,
            Texture2D? restoreTexture,
            ViewState target,
            Texture2D targetTexture,
            ImageTexture? capture,
            DirectionTransitionSpec transition,
            bool publishTransitionStart,
            ITransitionDriverRegistry drivers,
            IEasingRegistry? easings = null)
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
            this.drivers = drivers ?? new TransitionDriverRegistry();
            this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
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
            capture?.Dispose();
            return ValueTask.CompletedTask;
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
