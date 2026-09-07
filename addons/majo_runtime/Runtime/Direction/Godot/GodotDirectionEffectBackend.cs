#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionEffectBackend : IDirectionEffectBackend
{
    private readonly GodotDirectionRenderHost host;
    private readonly IEasingRegistry? easings;

    public GodotDirectionEffectBackend(
        GodotDirectionRenderHost host,
        IEasingRegistry? easings = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.easings = easings;
    }

    public Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StableEffectState before,
        StableEffectState target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transition = GodotDirectionRendering.ResolveTransition(
            plan,
            path => path.StartsWith("effect/", StringComparison.Ordinal));
        GodotEasingAdapter.Validate(transition.Easing, easings);
        return Task.FromResult<IPreparedDirectionComponent>(
            new PreparedEffect(host, before, target, transition, easings));
    }

    private sealed class PreparedEffect : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly StableEffectState before;
        private readonly StableEffectState target;
        private readonly DirectionTransitionSpec transition;
        private readonly IEasingRegistry? easings;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Tween? tween;
        private bool committed;
        private bool rolledBack;
        private bool disposed;
        private long generation;

        public PreparedEffect(
            GodotDirectionRenderHost host,
            StableEffectState before,
            StableEffectState target,
            DirectionTransitionSpec transition,
            IEasingRegistry? easings = null)
        {
            this.host = host;
            this.before = before;
            this.target = target;
            this.transition = transition;
            this.easings = easings;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("Effect is no longer preparable.");
            committed = true;
            generation = host.BeginEffectGeneration();
            if (transition.DurationSeconds <= 0 || transition.Kind == DirectionTransitionId.Cut)
            {
                Complete();
                return;
            }

            tween = host.CreateTween();
            GodotDirectionRendering.Configure(tween, transition.Easing, easings);
            tween.TweenMethod(
                Callable.From<float>(progress => GodotEasingAdapter.ApplyProgress(
                    transition.Easing, progress, easings, effectiveProgress =>
                {
                    if (!host.IsCurrentEffectGeneration(generation)) return;

                    var from = before.Curtain;
                    var to = target.Curtain;
                    host.PublishEffects(new StableEffectState(new CurtainState(
                        new DirectionColor(
                            Mathf.Lerp(from.Color.R, to.Color.R, effectiveProgress),
                            Mathf.Lerp(from.Color.G, to.Color.G, effectiveProgress),
                            Mathf.Lerp(from.Color.B, to.Color.B, effectiveProgress),
                            Mathf.Lerp(from.Color.A, to.Color.A, effectiveProgress)),
                        Mathf.Lerp(from.Opacity, to.Opacity, effectiveProgress))));
                }, exception =>
                {
                    GodotDirectionRendering.Kill(tween);
                    settlement.TrySetException(exception);
                })),
                0.0f,
                1.0f,
                transition.DurationSeconds);
            tween.Finished += OnFinished;
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed || rolledBack || disposed)
                throw new InvalidOperationException("Effect cannot finish in its current state.");
            Complete();
            return ValueTask.CompletedTask;
        }

        public void Rollback(DirectionCommitToken token)
        {
            if (rolledBack || disposed) return;
            rolledBack = true;
            if (tween is not null && GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
            if (host.IsCurrentEffectGeneration(generation))
                host.PublishEffects(before);
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
            if (host.IsCurrentEffectGeneration(generation))
                host.PublishEffects(target);
            settlement.TrySetResult();
        }
    }
}
