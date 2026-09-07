#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Timing;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionEffectBackend : IDirectionEffectBackend
{
    private readonly GodotDirectionRenderHost host;
    private readonly IEasingRegistry easings;

    public GodotDirectionEffectBackend(
        GodotDirectionRenderHost host,
        IEasingRegistry? easings = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
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
        easings.GetRequired(transition.Easing);
        return Task.FromResult<IPreparedDirectionComponent>(
            new PreparedEffect(host, before, target, transition, easings));
    }

    private sealed class PreparedEffect : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly StableEffectState before;
        private readonly StableEffectState target;
        private readonly DirectionTransitionSpec transition;
        private readonly IEasingRegistry easings;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private GodotProgressTween? clock;
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
            this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
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

            clock = host.Animate(transition, easings.GetRequired(transition.Easing), progress =>
            {
                if (!host.IsCurrentEffectGeneration(generation)) return;
                var p = Math.Clamp(progress, 0, 1);
                var from = before.Curtain;
                var to = target.Curtain;
                host.PublishEffects(new StableEffectState(new CurtainState(
                    new DirectionColor(Mathf.Lerp(from.Color.R, to.Color.R, p),
                        Mathf.Lerp(from.Color.G, to.Color.G, p), Mathf.Lerp(from.Color.B, to.Color.B, p),
                        Mathf.Lerp(from.Color.A, to.Color.A, p)), Mathf.Lerp(from.Opacity, to.Opacity, p))));
            }, OnFinished, exception => settlement.TrySetException(exception));
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
            clock?.Dispose();
            clock = null;
            if (host.IsCurrentEffectGeneration(generation))
                host.PublishEffects(before);
            settlement.TrySetCanceled();
        }

        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            if (!settlement.Task.IsCompleted) settlement.TrySetCanceled();
            clock?.Dispose();
            clock = null;
            return ValueTask.CompletedTask;
        }

        private void OnFinished() => Complete();

        private void Complete()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            clock?.Dispose();
            clock = null;
            if (host.IsCurrentEffectGeneration(generation))
                host.PublishEffects(target);
            settlement.TrySetResult();
        }
    }
}
