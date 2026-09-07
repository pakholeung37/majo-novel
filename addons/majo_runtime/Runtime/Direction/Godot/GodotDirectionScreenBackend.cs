#nullable enable

using System;
using System.Collections.Generic;
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

internal sealed class GodotDirectionScreenBackend : IDirectionScreenBackend
{
    private readonly GodotDirectionRenderHost host;
    private readonly ITransitionDriverRegistry drivers;
    private readonly IEasingRegistry easings;

    public GodotDirectionScreenBackend(
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
        ScreenState before,
        ScreenState target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);
        DirectionLayoutCatalog.RequiredSlots(target.Layout);
        var createdViews = new List<ViewId>();
        ImageTexture? source = null;
        try
        {
            foreach (var viewId in target.Bindings.Values)
            {
                if (!plan.TargetState.Views.ContainsKey(viewId))
                    throw new DirectionReferenceException(
                        $"Direction Screen references undeclared View '{viewId}'.");
                if (!host.HasView(viewId))
                {
                    host.GetOrCreateView(viewId);
                    createdViews.Add(viewId);
                }
            }
            var transition = GodotDirectionRendering.ResolveTransition(
                plan,
                path => path.StartsWith("screen/", StringComparison.Ordinal));
            GodotDirectionRendering.ValidateTransition(transition, TransitionDomains.Screen, drivers, easings);
            if (transition.DurationSeconds > 0 &&
                transition.Kind != DirectionTransitionId.Cut &&
                transition.Kind != DirectionTransitionId.Move)
            {
                source = await host.CaptureScreenAsync(cancellationToken);
            }
            IPreparedDirectionComponent prepared = new PreparedScreen(
                host, before, target, transition, source, drivers, easings);
            source = null;
            return createdViews.Count == 0
                ? prepared
                : new PreparedScreenLifecycle(host, createdViews, prepared);
        }
        catch
        {
            source?.Dispose();
            foreach (var viewId in createdViews)
                host.ReleasePreparedView(viewId);
            throw;
        }
    }

    private sealed class PreparedScreen : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly ScreenState before;
        private readonly ScreenState target;
        private readonly DirectionTransitionSpec transition;
        private ImageTexture? source;
        private readonly ITransitionDriverRegistry drivers;
        private readonly IEasingRegistry easings;
        private readonly TaskCompletionSource settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GodotProgressTween? clock;
        private IPreparedVisualTransition? preparedTransition;
        private long generation;
        private bool committed;
        private bool rolledBack;
        private bool disposed;

        public PreparedScreen(
            GodotDirectionRenderHost host,
            ScreenState before,
            ScreenState target,
            DirectionTransitionSpec transition,
            ImageTexture? source,
            ITransitionDriverRegistry? drivers = null,
            IEasingRegistry? easings = null)
        {
            this.host = host;
            this.before = before;
            this.target = target;
            this.transition = transition;
            this.source = source;
            this.drivers = drivers ?? new TransitionDriverRegistry();
            this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("Screen is no longer preparable.");
            committed = true;
            generation = host.BeginScreenGeneration();
            if (transition.DurationSeconds <= 0 || transition.Kind == DirectionTransitionId.Cut)
            {
                host.PublishScreen(target);
                settlement.TrySetResult();
                return;
            }

            if (transition.Kind == DirectionTransitionId.Move)
            {
                clock = host.Animate(transition, easings.GetRequired(transition.Easing), p =>
                {
                    if (host.IsCurrentScreenGeneration(generation)) host.PublishScreenLayout(target, before, p);
                }, OnFinished, exception => settlement.TrySetException(exception));
            }
            else
            {
                if (source is null) throw new DirectionException("Screen transition has no source capture.");
                host.PublishScreen(target);
                host.PublishScreenTransition(source);
                var context = new VisualTransitionContext(host.ScreenTransitionSource, transition, source,
                    host.ScreenViewport.GetTexture(), true, true,
                    () => host.IsCurrentScreenGeneration(generation));
                preparedTransition = drivers.GetRequired(transition.Kind).Prepare(context);
                if (transition.Kind != DirectionTransitionId.Fade) host.LiveOutput.Visible = false;
                clock = host.Animate(transition, easings.GetRequired(transition.Easing), p =>
                {
                    if (host.IsCurrentScreenGeneration(generation)) preparedTransition.ApplyProgress(p);
                }, OnFinished, exception => settlement.TrySetException(exception));
            }
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed || rolledBack || disposed)
                throw new InvalidOperationException("Screen cannot finish in its current state.");
            Complete();
            return ValueTask.CompletedTask;
        }

        public void Rollback(DirectionCommitToken token)
        {
            if (rolledBack || disposed) return;
            rolledBack = true;
            generation = host.BeginScreenGeneration();
            clock?.Dispose();
            clock = null;
            preparedTransition?.Dispose();
            preparedTransition = null;
            host.ClearScreenTransition();
            host.PublishScreen(before);
            DisposeSource();
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
            if (host.IsCurrentScreenGeneration(generation)) host.ClearScreenTransition();
            DisposeSource();
            return ValueTask.CompletedTask;
        }

        private void OnFinished() => Complete();
        private void Complete()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            clock?.Dispose();
            clock = null;
            if (host.IsCurrentScreenGeneration(generation))
            {
                preparedTransition?.ApplyProgress(1.0f);
                host.PublishScreen(target);
                host.ClearScreenTransition();
            }
            preparedTransition?.Dispose();
            preparedTransition = null;
            DisposeSource();
            settlement.TrySetResult();
        }

        private void DisposeSource()
        {
            source?.Dispose();
            source = null;
        }
    }

    private sealed class PreparedScreenLifecycle : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly IReadOnlyList<ViewId> createdViews;
        private readonly IPreparedDirectionComponent component;
        private bool committed;
        private bool commitStarted;
        private bool disposed;

        public PreparedScreenLifecycle(
            GodotDirectionRenderHost host,
            IReadOnlyList<ViewId> createdViews,
            IPreparedDirectionComponent component)
        {
            this.host = host;
            this.createdViews = createdViews;
            this.component = component;
        }

        public Task Settlement => component.Settlement;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || disposed)
                throw new InvalidOperationException("Screen lifecycle is no longer preparable.");
            commitStarted = true;
            try
            {
                component.Commit(token);
                committed = true;
            }
            catch
            {
                // The composite coordinator will call Rollback as well; keeping
                // commitStarted true makes this wrapper compensate on that path.
                throw;
            }
        }

        public void Rollback(DirectionCommitToken token)
        {
            if (!commitStarted) return;
            try { component.Rollback(token); }
            finally { committed = false; }
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken) =>
            component.FinishAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            List<Exception>? failures = null;
            try { await component.DisposeAsync(); }
            catch (Exception exception) { failures = new List<Exception> { exception }; }
            if (!committed)
            {
                foreach (var viewId in createdViews)
                {
                    try { host.ReleasePreparedView(viewId); }
                    catch (Exception exception) { (failures ??= new()).Add(exception); }
                }
            }
            if (failures is not null)
                throw new AggregateException("Screen component cleanup failed.", failures);
        }
    }
}
