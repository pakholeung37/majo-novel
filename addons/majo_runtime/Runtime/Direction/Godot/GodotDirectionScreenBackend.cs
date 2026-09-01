#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionScreenBackend : IDirectionScreenBackend
{
    private readonly GodotDirectionRenderHost host;

    public GodotDirectionScreenBackend(GodotDirectionRenderHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public Task<IPreparedDirectionComponent> PrepareAsync(
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
            IPreparedDirectionComponent prepared = new PreparedScreen(
                host, before, target, transition);
            return Task.FromResult<IPreparedDirectionComponent>(
                createdViews.Count == 0
                    ? prepared
                    : new PreparedScreenLifecycle(host, createdViews, prepared));
        }
        catch
        {
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
        private readonly TaskCompletionSource settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Tween? tween;
        private long generation;
        private bool committed;
        private bool rolledBack;
        private bool disposed;

        public PreparedScreen(GodotDirectionRenderHost host, ScreenState before, ScreenState target, DirectionTransitionSpec transition)
        {
            this.host = host; this.before = before; this.target = target; this.transition = transition;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("Screen is no longer preparable.");
            committed = true;
            generation = host.BeginScreenGeneration();
            if (transition.DurationSeconds <= 0 || transition.Kind == DirectionTransitionKind.Cut)
            {
                host.PublishScreen(target);
                settlement.TrySetResult();
                return;
            }

            tween = host.CreateTween();
            GodotDirectionRendering.Configure(tween, transition.Ease);
            tween.TweenMethod(Callable.From<float>(progress =>
            {
                if (host.IsCurrentScreenGeneration(generation))
                    host.PublishScreenLayout(target, before, progress);
            }), 0.0f, 1.0f, transition.DurationSeconds);
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
            host.PublishScreen(before);
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
            return ValueTask.CompletedTask;
        }

        private void OnFinished() => Complete();
        private void Complete()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            if (tween is not null && GodotObject.IsInstanceValid(tween)) tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            if (host.IsCurrentScreenGeneration(generation)) host.PublishScreen(target);
            settlement.TrySetResult();
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
