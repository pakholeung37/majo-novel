#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionViewBackend : IDirectionViewBackend
{
    private static readonly string[] Channels = ["pose", "parallax", "idle"];
    private static readonly string[] WholeViewChannel = ["all"];
    private readonly Dictionary<(ViewId View, string Channel), PreparedView> active = new();
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
        var components = new List<IPreparedDirectionComponent>();
        try
        {
            Texture2D? beforeTexture = before is null
                ? null
                : host.GetStageTexture(before.StageId);
            Texture2D targetTexture = host.GetStageTexture(target.StageId);

            await host.WaitForRenderFenceAsync(cancellationToken);
            var wholeView = before is null || plan.IsStateReplacement || before.StageId != target.StageId ||
                plan.ViewIntents.Any(intent => intent.ViewId == viewId && intent.Parallax is not null &&
                    intent.Transition.Kind != DirectionTransitionId.Move && intent.Transition.Kind != DirectionTransitionId.Cut);
            var channels = wholeView ? WholeViewChannel : Channels
                .Where(channel => plan.Transitions.Any(t => t.StablePaths.Contains($"view/{viewId.Value}/{channel}")))
                .ToArray();
            foreach (var channel in channels)
            {
                var transition = GodotDirectionRendering.ResolveTransition(
                    plan,
                    path => channel == "all" ? path.StartsWith($"view/{viewId.Value}/", StringComparison.Ordinal)
                        : path == $"view/{viewId.Value}/{channel}");
                GodotDirectionRendering.ValidateTransition(transition, TransitionDomains.View, drivers, easings);
                var useLiveSource = before is not null && before.StageId == target.StageId &&
                    !plan.IsStateReplacement && transition.Kind != DirectionTransitionId.Cut && transition.Kind != DirectionTransitionId.Move &&
                    transition.DurationSeconds > 0;
                ImageTexture? capture = null;
                if (!useLiveSource && transition.DurationSeconds > 0 &&
                    transition.Kind != DirectionTransitionId.Cut &&
                    transition.Kind != DirectionTransitionId.Move)
                {
                    capture = await host.CaptureViewAsync(viewId, cancellationToken,
                        active.TryGetValue((viewId, channel), out var previous) && !previous.IsMotion);
                }

                var source = before ?? target;
                if (before is not null)
                    source = host.GetCurrentViewState(viewId, source);
                var prepared = new PreparedView(
                    this,
                    channel,
                    plan,
                    host,
                    viewId,
                    before,
                    source,
                    beforeTexture,
                    target,
                    targetTexture,
                    capture,
                    useLiveSource,
                    transition,
                    drivers,
                    easings);
                components.Add(prepared);
                await prepared.PrepareSourceAsync(cancellationToken);
            }
            var group = new GodotPreparedDirectionComponentGroup(components.ToArray());
            return createdView ? new PreparedViewLifecycle(host, viewId, group) : group;
        }
        catch
        {
            foreach (var component in components) await component.DisposeAsync();
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
        private readonly string channel;
        private (ViewId View, string Channel) Key => (viewId, channel);
        private bool OwnsPose => channel is "pose" or "all";
        private bool IsCurrent => backend.active.TryGetValue(Key, out var current) && ReferenceEquals(current, this)
            && host.IsCurrentViewGeneration(viewId, generation, channel);
        private readonly DirectionPlan plan;
        private ViewState before;
        private readonly Texture2D? restoreTexture;
        private ViewState target;
        private readonly Texture2D targetTexture;
        private readonly ImageTexture? capture;
        private readonly bool useLiveSource;
        private GodotDirectionViewSurface? liveSource;
        private GodotVisualInput? sourceInput;
        private GodotViewRenderDemand.Request? sourcePreparation;
        private IDisposable? sourceDependency;
        public bool IsMotion => transition.Kind == DirectionTransitionId.Move;
        private readonly DirectionTransitionSpec transition;
        private readonly ITransitionDriverRegistry drivers;
        private readonly IEasingRegistry easings;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private GodotProgressTween? clock;
        private GodotVisualTransition? preparedTransition;
        private bool committed;
        private bool rolledBack;
        private bool disposed;
        private long generation;

        public PreparedView(
            GodotDirectionViewBackend backend,
            string channel,
            DirectionPlan plan,
            GodotDirectionRenderHost host,
            ViewId viewId,
            ViewState? restoreState,
            ViewState before,
            Texture2D? restoreTexture,
            ViewState target,
            Texture2D targetTexture,
            ImageTexture? capture,
            bool useLiveSource,
            DirectionTransitionSpec transition,
            ITransitionDriverRegistry drivers,
            IEasingRegistry? easings = null)
        {
            this.host = host;
            this.channel = channel;
            this.viewId = viewId;
            this.backend = backend;
            this.plan = plan;
            this.restoreState = restoreState;
            this.before = before;
            this.restoreTexture = restoreTexture;
            this.target = target;
            this.targetTexture = targetTexture;
            this.capture = capture;
            this.useLiveSource = useLiveSource;
            this.transition = transition;
            this.drivers = drivers ?? new TransitionDriverRegistry();
            this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
        }

        public async Task PrepareSourceAsync(CancellationToken token)
        {
            if (!useLiveSource) return;
            liveSource = host.CreateLiveViewSource(viewId, before);
            sourcePreparation = host.AcquireViewDemand(liveSource, "camera preparation");
            sourceInput = GodotVisualInput.Live(liveSource.CompositeViewport,
                new Rect2(Vector2.Zero, liveSource.CompositeViewport.Size), ReleaseLiveSource);
            await host.WaitForRenderedViewAsync(liveSource, sourcePreparation, token);
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("View is no longer preparable.");
            committed = true;
            foreach (var previous in backend.active.Where(pair => pair.Key.View == viewId &&
                (channel == "all" || pair.Key.Channel == "all" || pair.Key.Channel == channel)).Select(pair => pair.Value).ToArray())
            {
                // A visual mix is completed before replacement; motion is sampled in place.
                if (previous.transition.Kind == DirectionTransitionId.Move) previous.Supersede();
                else previous.Complete();
            }
            before = host.GetCurrentViewState(viewId, before);
            var intent = plan.ViewIntents.SingleOrDefault(patch => patch.ViewId == viewId);
            if (OwnsPose && intent is not null)
            {
                target = target with
                {
                    Pose = before.Pose with
                    {
                        Center = intent.Center.HasValue ? intent.Center.Value : before.Pose.Center,
                        Zoom = intent.Zoom.HasValue ? intent.Zoom.Value : before.Pose.Zoom,
                        RotationDegrees = intent.RotationDegrees.HasValue ? intent.RotationDegrees.Value : before.Pose.RotationDegrees,
                        Dolly = intent.Dolly.HasValue ? intent.Dolly.Value : before.Pose.Dolly,
                        BackgroundOffset = intent.BackgroundOffset.HasValue ? intent.BackgroundOffset.Value : before.Pose.BackgroundOffset,
                        BackgroundScale = intent.BackgroundScale.HasValue ? intent.BackgroundScale.Value : before.Pose.BackgroundScale,
                    }
                };
                plan.CommitViews[viewId] = target;
            }
            backend.active[Key] = this;
            generation = host.BeginViewGeneration(viewId, channel);
            if (OwnsPose) host.ClearViewTransition(viewId);
            var transitionSource = host.GetViewTransitionSource(viewId);

            if (liveSource is not null)
            {
                host.UpdateLiveViewSource(liveSource, viewId, before);
                sourceDependency = host.AcquireViewDependency(host.RequireView(viewId), liveSource);
                sourcePreparation?.Dispose();
                sourcePreparation = null;
            }
            Texture2D? sourceTexture = liveSource?.CompositeViewport.GetTexture() ?? (Texture2D?)capture;
            if (sourceTexture is not null)
            {
                transitionSource.Texture = sourceTexture;
                transitionSource.Modulate = Colors.White;
                transitionSource.Visible = true;
            }

            Publish(target);
            if (transition.DurationSeconds <= 0 || transition.Kind == DirectionTransitionId.Cut)
            {
                Complete();
                return;
            }

            if (transition.Kind == DirectionTransitionId.Move)
            {
                Apply(before);
                clock = host.Animate(transition, easings.GetRequired(transition.Easing), p =>
                {
                    if (IsCurrent)
                        Apply(target with
                        {
                            Pose = GodotDirectionRendering.Interpolate(before.Pose, target.Pose, p),
                            Parallax = GodotDirectionRendering.Interpolate(before.Parallax, target.Parallax, p),
                            Idle = GodotDirectionRendering.Interpolate(before.Idle, target.Idle, p),
                        });
                }, OnFinished, exception => settlement.TrySetException(exception));
            }
            else
            {
                if (sourceTexture is null) { Complete(); return; }
                var surface = host.GetOrCreateView(viewId);
                sourceInput ??= GodotVisualInput.Frozen(sourceTexture, new Rect2(Vector2.Zero, sourceTexture.GetSize()));
                var context = new VisualTransitionContext(transitionSource, transition, IsActive: () => IsCurrent);
                preparedTransition = GodotVisualTransition.Prepare(drivers.GetRequired(transition.Kind), context,
                    sourceInput, GodotVisualInput.Live(surface.CompositeViewport, new Rect2(Vector2.Zero, surface.CompositeViewport.Size)));
                if (transition.Kind != DirectionTransitionId.Fade) surface.CompositeOutput.Visible = false;
                clock = host.Animate(transition, easings.GetRequired(transition.Easing), p =>
                {
                    if (IsCurrent) preparedTransition.ApplyProgress(p);
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
            var wasCurrent = IsCurrent;
            clock?.Dispose();
            clock = null;
            preparedTransition?.Dispose();
            preparedTransition = null;
            if (wasCurrent)
            {
                if (OwnsPose) host.ClearViewTransition(viewId);
                if (restoreState is not null && restoreTexture is not null)
                    host.PublishView(viewId, Merge(restoreState), restoreTexture, channel == "all");
                Retire();
            }
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
            if (IsCurrent && OwnsPose)
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

        private void ReleaseLiveSource()
        {
            sourceDependency?.Dispose();
            sourceDependency = null;
            sourcePreparation?.Dispose();
            sourcePreparation = null;
            if (liveSource is not null)
            {
                host.ReleaseLiveViewSource(liveSource);
                liveSource = null;
            }
        }

        private void Retire()
        {
            sourceInput?.Dispose(); sourceInput = null;
            ReleaseLiveSource();
            if (backend.active.TryGetValue(Key, out var current) && ReferenceEquals(current, this))
                backend.active.Remove(Key);
        }

        private void OnFinished() => Complete();

        private void Complete()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            clock?.Dispose();
            clock = null;
            if (IsCurrent)
            {
                preparedTransition?.ApplyProgress(1.0f);
                Publish(target);
                if (OwnsPose) host.ClearViewTransition(viewId);
            }
            preparedTransition?.Dispose();
            preparedTransition = null;
            Retire();
            settlement.TrySetResult();
        }

        private ViewState Merge(ViewState value)
        {
            if (channel == "all") return value;
            var current = host.GetCurrentViewState(viewId, value);
            return current with
            {
                Pose = channel == "pose" ? value.Pose : current.Pose,
                Parallax = channel == "parallax" ? value.Parallax : current.Parallax,
                Idle = channel == "idle" ? value.Idle : current.Idle,
            };
        }

        private void Apply(ViewState value)
        {
            var merged = Merge(value);
            host.ApplyViewState(viewId, merged.Pose, merged.Parallax, merged.Idle);
        }

        private void Publish(ViewState value) =>
            host.PublishView(viewId, Merge(value), targetTexture, plan.IsStateReplacement);
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
