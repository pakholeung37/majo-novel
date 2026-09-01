#nullable enable

using System;
using System.Collections.Generic;
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
    private readonly Func<Node2D, IActorManager>? createActorManager;
    private readonly Dictionary<StageId, GodotDirectionActorTreeBackend> actors = new();

    public GodotDirectionStageBackend(
        GodotDirectionRenderHost host,
        IDirectionTextureLoader textures,
        Func<Node2D, IActorManager>? createActorManager = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
        this.createActorManager = createActorManager;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        foreach (var backend in actors.Values)
            await backend.ResetAsync(cancellationToken);
        actors.Clear();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var backend in actors.Values)
            await backend.ResetAsync(cancellationToken);
        actors.Clear();
    }

    public async Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StageId stageId,
        StageState? before,
        StageState? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var createdSurface = !host.HasStage(stageId);
        var surface = host.GetOrCreateStage(stageId);

        var backdropChanged = !Equals(before?.Backdrop, target?.Backdrop);
        var actorsChanged = !DirectionActorsEqual(before, target);
        if (!backdropChanged && !actorsChanged)
            return new ImmediatePreparedDirectionComponent();

        IPreparedDirectionComponent? preparedBackdrop = null;
        IPreparedDirectionComponent? preparedActors = null;
        try
        {
            if (backdropChanged)
                preparedBackdrop = await PrepareBackdropAsync(
                    plan,
                    stageId,
                    target,
                    cancellationToken);
            if (actorsChanged)
            {
                if (createActorManager is null)
                {
                    throw new DirectionException(
                        "Direction Actor state changed, but no actor manager factory is configured.");
                }

                if (!actors.TryGetValue(stageId, out var actorBackend))
                {
                    actorBackend = new GodotDirectionActorTreeBackend(
                        host,
                        surface.Actors,
                        createActorManager);
                    actors.Add(stageId, actorBackend);
                }

                preparedActors = await actorBackend.PrepareAsync(
                    plan,
                    before,
                    target,
                    cancellationToken);
            }

            IPreparedDirectionComponent component = preparedBackdrop switch
            {
                null => preparedActors!,
                _ when preparedActors is null => preparedBackdrop,
                _ => new GodotPreparedDirectionComponentGroup(
                    preparedBackdrop,
                    preparedActors),
            };
            return createdSurface
                ? new PreparedStageLifecycle(this, stageId, component)
                : component;
        }
        catch
        {
            if (preparedActors is not null) await preparedActors.DisposeAsync();
            if (preparedBackdrop is not null) await preparedBackdrop.DisposeAsync();
            if (createdSurface) await ReleasePreparedStageAsync(stageId);
            throw;
        }
    }

    private async Task<IPreparedDirectionComponent> PrepareBackdropAsync(
        DirectionPlan plan,
        StageId stageId,
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

        var surface = host.GetStage(stageId);
        var transition = GodotDirectionRendering.ResolveTransition(
            plan,
            path => string.Equals(
                path,
                $"stage/{stageId.Value}/backdrop",
                StringComparison.Ordinal));
        return new PreparedBackdrop(
            host,
            surface,
            surface.PublishedBackdrop,
            surface.Backdrop.Texture,
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

    private async ValueTask ReleasePreparedStageAsync(StageId stageId)
    {
        if (actors.Remove(stageId, out var actorBackend))
            await actorBackend.ResetAsync(CancellationToken.None);
        host.ReleasePreparedStage(stageId);
    }

    private sealed class PreparedStageLifecycle : IPreparedDirectionComponent
    {
        private readonly GodotDirectionStageBackend backend;
        private readonly StageId stageId;
        private readonly IPreparedDirectionComponent component;
        private bool committed;
        private bool disposed;

        public PreparedStageLifecycle(
            GodotDirectionStageBackend backend,
            StageId stageId,
            IPreparedDirectionComponent component)
        {
            this.backend = backend;
            this.stageId = stageId;
            this.component = component;
        }

        public Task Settlement => component.Settlement;

        public void Commit(DirectionCommitToken token)
        {
            component.Commit(token);
            committed = true;
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken) =>
            component.FinishAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await component.DisposeAsync();
            if (!committed) await backend.ReleasePreparedStageAsync(stageId);
        }
    }

    private sealed class PreparedBackdrop : IPreparedDirectionComponent
    {
        private readonly GodotDirectionRenderHost host;
        private readonly GodotDirectionStageSurface surface;
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
            GodotDirectionStageSurface surface,
            BackdropState? source,
            Texture2D? sourceTexture,
            BackdropState? target,
            Texture2D? targetTexture,
            DirectionTransitionSpec transition)
        {
            this.host = host;
            this.surface = surface;
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
            host.ClearBackdropTransition(surface.Id);
            var animate = transition.DurationSeconds > 0 &&
                transition.Kind is DirectionTransitionKind.Fade or
                    DirectionTransitionKind.Dissolve &&
                sourceTexture is not null &&
                source is not null;
            if (animate)
            {
                host.PublishBackdropTransition(surface.Id, source!, sourceTexture!);
            }

            host.PublishBackdrop(surface.Id, target, targetTexture);
            if (!animate)
            {
                settlement.TrySetResult();
                return;
            }

            tween = host.CreateTween();
            GodotDirectionRendering.Configure(tween, transition.Ease);
            tween.TweenProperty(
                surface.BackdropTransition,
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
            host.PublishBackdrop(surface.Id, target, targetTexture);
            host.ClearBackdropTransition(surface.Id);
            settlement.TrySetResult();
        }
    }
}
