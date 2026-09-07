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
using Majo.Runtime.Timing;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionScreenBackend : IDirectionScreenBackend
{
    private readonly GodotDirectionRenderHost host;
    private readonly IEasingRegistry? easings;

    public GodotDirectionScreenBackend(
        GodotDirectionRenderHost host,
        IEasingRegistry? easings = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.easings = easings;
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
            GodotEasingAdapter.Validate(transition.Easing, easings);
            if (transition.DurationSeconds > 0 &&
                transition.Kind != DirectionTransitionId.Cut &&
                transition.Kind != DirectionTransitionId.Move)
            {
                source = await host.CaptureScreenAsync(cancellationToken);
            }
            IPreparedDirectionComponent prepared = new PreparedScreen(
                host, before, target, transition, source, easings);
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
        private readonly IEasingRegistry? easings;
        private readonly TaskCompletionSource settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Tween? tween;
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
            IEasingRegistry? easings = null)
        {
            this.host = host;
            this.before = before;
            this.target = target;
            this.transition = transition;
            this.source = source;
            this.easings = easings;
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
                tween = host.CreateTween();
                GodotDirectionRendering.Configure(tween, transition.Easing, easings);
                tween.TweenMethod(Callable.From<float>(progress => GodotEasingAdapter.ApplyProgress(
                    transition.Easing, progress, easings, effectiveProgress =>
                {
                    if (host.IsCurrentScreenGeneration(generation))
                    {

                        host.PublishScreenLayout(target, before, effectiveProgress);
                    }
                }, exception =>
                {
                    GodotDirectionRendering.Kill(tween);
                    settlement.TrySetException(exception);
                })), 0.0f, 1.0f, transition.DurationSeconds);
            }
            else
            {
                if (source is null)
                    throw new DirectionException(
                        "Direction Screen transition committed without a source capture.");
                host.PublishScreen(target);
                host.PublishScreenTransition(source);
                tween = host.CreateTween();
                GodotDirectionRendering.Configure(tween, transition.Easing, easings);
                tween.TweenMethod(Callable.From<float>(progress => GodotEasingAdapter.ApplyProgress(
                    transition.Easing, progress, easings, effectiveProgress =>
                {
                    if (host.IsCurrentScreenGeneration(generation))
                    {

                        host.SetScreenTransitionOpacity(1.0f - effectiveProgress);
                    }
                }, exception =>
                {
                    GodotDirectionRendering.Kill(tween);
                    settlement.TrySetException(exception);
                })), 0.0f, 1.0f, transition.DurationSeconds);
            }
            tween.Finished += OnFinished;
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
            if (tween is not null && GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
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
            if (tween is not null && GodotObject.IsInstanceValid(tween)) tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            if (!committed) DisposeSource();
            return ValueTask.CompletedTask;
        }

        private void OnFinished() => Complete();
        private void Complete()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            if (tween is not null && GodotObject.IsInstanceValid(tween)) tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            if (host.IsCurrentScreenGeneration(generation))
            {
                host.PublishScreen(target);
                host.ClearScreenTransition();
            }
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
