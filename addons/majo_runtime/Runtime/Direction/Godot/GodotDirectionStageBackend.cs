#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionStageBackend : IDirectionStageBackend, IRuntimeService
{
    private readonly GodotDirectionRenderHost host;
    private readonly IDirectionTextureLoader textures;
    private readonly GodotDirectionActorTreeBackend? actors;

    public GodotDirectionStageBackend(
        GodotDirectionRenderHost host,
        IDirectionTextureLoader textures,
        Func<Node2D, IActorManager>? createActorManager = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
        actors = createActorManager is null
            ? null
            : new GodotDirectionActorTreeBackend(host, createActorManager);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        if (actors is not null) await actors.ResetAsync(cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (actors is not null) await actors.ResetAsync(cancellationToken);
    }

    public async Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StageId stageId,
        StageState? before,
        StageState? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (stageId != StageId.Main)
        {
            throw new DirectionReferenceException(
                $"The first Godot Direction backend only supports Stage '{StageId.Main}'.");
        }

        var backdropChanged = !Equals(before?.Backdrop, target?.Backdrop);
        var actorsChanged = !DirectionActorsEqual(before, target);
        if (!backdropChanged && !actorsChanged)
            return new ImmediatePreparedDirectionComponent();

        IPreparedDirectionComponent? preparedBackdrop = null;
        IPreparedDirectionComponent? preparedActors = null;
        try
        {
            if (backdropChanged)
                preparedBackdrop = await PrepareBackdropAsync(plan, target, cancellationToken);
            if (actorsChanged)
            {
                if (actors is null)
                {
                    throw new DirectionException(
                        "Direction Actor state changed, but no actor manager factory is configured.");
                }

                preparedActors = await actors.PrepareAsync(
                    plan,
                    before,
                    target,
                    cancellationToken);
            }

            if (preparedBackdrop is null) return preparedActors!;
            if (preparedActors is null) return preparedBackdrop;
            return new GodotPreparedDirectionComponentGroup(preparedBackdrop, preparedActors);
        }
        catch
        {
            if (preparedActors is not null) await preparedActors.DisposeAsync();
            if (preparedBackdrop is not null) await preparedBackdrop.DisposeAsync();
            throw;
        }
    }

    private async Task<IPreparedDirectionComponent> PrepareBackdropAsync(
        DirectionPlan plan,
        StageState? target,
        CancellationToken cancellationToken)
    {
        Texture2D? targetTexture = null;
        if (target?.Backdrop is not null)
        {
            targetTexture = await textures.LoadAsync(
                target.Backdrop.Resource,
                cancellationToken);
        }

        var transition = GodotDirectionRendering.ResolveTransition(
            plan,
            path => string.Equals(path, "stage/main/backdrop", StringComparison.Ordinal));
        return new PreparedBackdrop(
            host,
            host.PublishedBackdrop,
            host.Backdrop.Texture,
            target?.Backdrop,
            targetTexture,
            transition);
    }

    private static bool DirectionActorsEqual(StageState? before, StageState? target)
    {
        var left = before?.Actors;
        var right = target?.Actors;
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        return left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            Equals(pair.Value, value));
    }

    private sealed class PreparedBackdrop : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly BackdropState? source;
        private readonly Texture2D? sourceTexture;
        private readonly BackdropState? target;
        private readonly Texture2D? targetTexture;
        private readonly DirectionTransitionSpec transition;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Tween? tween;
        private bool committed;

        public PreparedBackdrop(
            GodotDirectionRenderHost host,
            BackdropState? source,
            Texture2D? sourceTexture,
            BackdropState? target,
            Texture2D? targetTexture,
            DirectionTransitionSpec transition)
        {
            this.host = host;
            this.source = source;
            this.sourceTexture = sourceTexture;
            this.target = target;
            this.targetTexture = targetTexture;
            this.transition = transition;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed) throw new InvalidOperationException("Backdrop already committed.");
            committed = true;
            host.ClearBackdropTransition();
            var animate = transition.DurationSeconds > 0 &&
                transition.Kind is DirectionTransitionKind.Fade or
                    DirectionTransitionKind.Dissolve &&
                sourceTexture is not null &&
                source is not null;
            if (animate)
            {
                host.PublishBackdropTransition(source!, sourceTexture!);
            }

            host.PublishBackdrop(target, targetTexture);
            if (!animate)
            {
                settlement.TrySetResult();
                return;
            }

            tween = host.CreateTween();
            GodotDirectionRendering.Configure(tween, transition.Ease);
            tween.TweenProperty(
                host.BackdropTransition,
                "modulate:a",
                0,
                transition.DurationSeconds);
            tween.Finished += OnFinished;
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed) throw new InvalidOperationException("Backdrop has not committed.");
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
            host.PublishBackdrop(target, targetTexture);
            host.ClearBackdropTransition();
            settlement.TrySetResult();
        }
    }
}
