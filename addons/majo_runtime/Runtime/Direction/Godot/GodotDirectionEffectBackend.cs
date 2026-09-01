#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionEffectBackend : IDirectionEffectBackend
{
    private readonly GodotDirectionRenderHost host;

    public GodotDirectionEffectBackend(GodotDirectionRenderHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
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
        return Task.FromResult<IPreparedDirectionComponent>(
            new PreparedEffect(host, before, target, transition));
    }

    private sealed class PreparedEffect : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly StableEffectState before;
        private readonly StableEffectState target;
        private readonly DirectionTransitionSpec transition;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Tween? tween;
        private bool committed;

        public PreparedEffect(
            GodotDirectionRenderHost host,
            StableEffectState before,
            StableEffectState target,
            DirectionTransitionSpec transition)
        {
            this.host = host;
            this.before = before;
            this.target = target;
            this.transition = transition;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed) throw new InvalidOperationException("Effect already committed.");
            committed = true;
            if (transition.DurationSeconds <= 0 || transition.Kind == DirectionTransitionKind.Cut)
            {
                Complete();
                return;
            }

            tween = host.CreateTween();
            GodotDirectionRendering.Configure(tween, transition.Ease);
            tween.TweenMethod(
                Callable.From<float>(progress =>
                {
                    var from = before.Curtain;
                    var to = target.Curtain;
                    host.PublishEffects(new StableEffectState(new CurtainState(
                        new DirectionColor(
                            Mathf.Lerp(from.Color.R, to.Color.R, progress),
                            Mathf.Lerp(from.Color.G, to.Color.G, progress),
                            Mathf.Lerp(from.Color.B, to.Color.B, progress),
                            Mathf.Lerp(from.Color.A, to.Color.A, progress)),
                        Mathf.Lerp(from.Opacity, to.Opacity, progress))));
                }),
                0.0f,
                1.0f,
                transition.DurationSeconds);
            tween.Finished += OnFinished;
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed) throw new InvalidOperationException("Effect has not committed.");
            Complete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!committed) settlement.TrySetCanceled();
            if (tween is not null && GodotObject.IsInstanceValid(tween))
                tween.Finished -= OnFinished;
            GodotDirectionRendering.Kill(tween);
            tween = null;
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
            host.PublishEffects(target);
            settlement.TrySetResult();
        }
    }
}
