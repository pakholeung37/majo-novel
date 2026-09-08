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
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Timing;
using Majo.Runtime.Operations.Godot;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionStageBackend : IDirectionStageBackend, IRuntimeService
{
    private readonly Dictionary<StageId, PreparedBackdrop> activeBackdrops = new();
    private readonly GodotDirectionRenderHost host;
    private readonly IDirectionTextureLoader textures;
    private readonly Func<Node2D, IActorManager>? createActorManager;
    private readonly ITransitionDriverRegistry drivers;
    private readonly IEasingRegistry easings;
    private readonly Dictionary<StageId, GodotDirectionActorTreeBackend> actors = new();

    public GodotDirectionStageBackend(
        GodotDirectionRenderHost host,
        IDirectionTextureLoader textures,
        Func<Node2D, IActorManager>? createActorManager = null,
        ITransitionDriverRegistry? drivers = null,
        IEasingRegistry? easings = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.textures = textures ?? throw new ArgumentNullException(nameof(textures));
        this.createActorManager = createActorManager;
        this.drivers = drivers ?? new TransitionDriverRegistry();
        this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        foreach (var backend in actors.Values.ToArray())
        {
            try { await backend.ResetAsync(cancellationToken); }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }
        }
        actors.Clear();
        if (failures is not null)
            throw new AggregateException("One or more Direction stage actor trees failed to reset.", failures);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        foreach (var backend in actors.Values.ToArray())
        {
            try { await backend.ResetAsync(cancellationToken); }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }
        }
        actors.Clear();
        if (failures is not null)
            throw new AggregateException("One or more Direction stage actor trees failed to shut down.", failures);
    }

    public async Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StageId stageId,
        StageState? before,
        StageState? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (target is null)
        {
            if (before is null)
                throw new DirectionReferenceException(
                    $"Direction Stage '{stageId}' has neither source nor target state.");
            return new PreparedStageDeletion(this, stageId);
        }
        var createdSurface = !host.HasStage(stageId);
        var surface = host.GetOrCreateStage(stageId);

        var backdropChanged = !Equals(before?.Backdrop, target?.Backdrop) || plan.Transitions.Any(transition =>
            transition.StablePaths.Contains($"stage/{stageId.Value}/backdrop"));
        var actorsChanged = !DirectionActorsEqual(before, target) || plan.ActorIntents.Any(patch => patch switch
        {
            ActorPatch value => value.StageId == stageId,
            ActorMovePatch value => value.StageId == stageId,
            ActorHidePatch value => value.StageId == stageId,
            _ => false,
        });
        if (!backdropChanged && !actorsChanged)
        {
            var immediate = new ImmediatePreparedDirectionComponent();
            return createdSurface
                ? new PreparedStageLifecycle(this, stageId, immediate)
                : immediate;
        }

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
                        createActorManager,
                        easings, drivers);
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
        catch (Exception preparationException)
        {
            List<Exception>? failures = null;
            foreach (var prepared in new[] { preparedActors, preparedBackdrop })
            {
                if (prepared is null) continue;
                try { await prepared.DisposeAsync(); }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
            if (createdSurface)
            {
                try { await ReleasePreparedStageAsync(stageId); }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
            if (failures is not null)
                throw new AggregateException(
                    "Direction stage preparation failed and cleanup also failed.",
                    new[] { preparationException }.Concat(failures));
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
        GodotDirectionRendering.ValidateTransition(transition, TransitionDomains.Stage, drivers, easings);
        var prepared = new PreparedBackdrop(
            this,
            host,
            surface,
            surface.PublishedBackdrop,
            surface.Backdrop.Texture,
            target?.Backdrop,
            targetTexture,
            transition,
            drivers,
            easings);
        try
        {
            await prepared.PrepareInputsAsync(cancellationToken);
            return prepared;
        }
        catch { await prepared.DisposeAsync(); throw; }

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
        Exception? failure = null;
        if (actors.Remove(stageId, out var actorBackend))
        {
            try { await actorBackend.ResetAsync(CancellationToken.None); }
            catch (Exception exception) { failure = exception; }
        }
        try
        {
            if (host.HasStage(stageId)) await host.ReleaseActorModifierTargetAsync(
                new ActorFxTarget(stageId, ActorFxSlot.Backdrop), host.GetStage(stageId).ModifierTarget);
            host.ReleasePreparedStage(stageId);
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        if (failure is not null) throw failure;
    }

    private sealed class PreparedStageDeletion : IPreparedDirectionComponent
    {
        private readonly GodotDirectionStageBackend backend;
        private readonly StageId stageId;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool committed;
        private bool disposed;

        public PreparedStageDeletion(GodotDirectionStageBackend backend, StageId stageId)
        {
            this.backend = backend;
            this.stageId = stageId;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || disposed)
                throw new InvalidOperationException("Stage deletion is no longer preparable.");
            committed = true;
            settlement.TrySetResult();
        }

        public void Rollback(DirectionCommitToken token) => committed = false;

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed || disposed)
                throw new InvalidOperationException("Stage deletion cannot finish in its current state.");
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            if (!settlement.Task.IsCompleted) settlement.TrySetCanceled();
            if (committed) await backend.ReleasePreparedStageAsync(stageId);
        }
    }

    private sealed class PreparedStageLifecycle : IPreparedDirectionComponent
    {
        private readonly GodotDirectionStageBackend backend;
        private readonly StageId stageId;
        private readonly IPreparedDirectionComponent component;
        private bool committed;
        private bool commitStarted;
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
            if (committed)
                throw new InvalidOperationException("Stage component already committed.");
            commitStarted = true;
            component.Commit(token);
            committed = true;
        }

        public void ConfirmCommit() => component.ConfirmCommit();

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
                try { await backend.ReleasePreparedStageAsync(stageId); }
                catch (Exception exception) { (failures ??= new()).Add(exception); }
            }
            if (failures is not null)
                throw new AggregateException("Stage component cleanup failed.", failures);
        }
    }

    private sealed class PreparedBackdrop : IPreparedDirectionComponent
    {
        private readonly GodotDirectionStageBackend backend;
        private readonly TaskCompletionSource<bool> confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GodotDirectionRenderHost host;
        private readonly GodotDirectionStageSurface surface;
        private readonly BackdropState? source;
        private readonly Texture2D? sourceTexture;
        private readonly BackdropState? target;
        private readonly Texture2D? targetTexture;
        private readonly DirectionTransitionSpec transition;
        private readonly ITransitionDriverRegistry drivers;
        private readonly IEasingRegistry easings;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private GodotProgressTween? clock;
        private IPreparedVisualTransition? preparedTransition;
        private SubViewport? sourceInput;
        private SubViewport? targetInput;
        private bool committed;
        private bool rolledBack;
        private bool disposed;
        private long generation;

        public PreparedBackdrop(
            GodotDirectionStageBackend backend,
            GodotDirectionRenderHost host,
            GodotDirectionStageSurface surface,
            BackdropState? source,
            Texture2D? sourceTexture,
            BackdropState? target,
            Texture2D? targetTexture,
            DirectionTransitionSpec transition,
            ITransitionDriverRegistry drivers,
            IEasingRegistry? easings = null)
        {
            this.host = host;
            this.backend = backend;
            this.surface = surface;
            this.source = source;
            this.sourceTexture = sourceTexture;
            this.target = target;
            this.targetTexture = targetTexture;
            this.transition = transition;
            this.drivers = drivers ?? new TransitionDriverRegistry();
            this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
        }

        public async Task PrepareInputsAsync(CancellationToken cancellationToken)
        {
            if (transition.DurationSeconds <= 0 || transition.Kind == DirectionTransitionId.Cut ||
                transition.Kind == DirectionTransitionId.Fade) return;
            sourceInput = host.PrepareBackdropInput(surface, source, sourceTexture);
            targetInput = host.PrepareBackdropInput(surface, target, targetTexture);
            await host.WaitForRenderFenceAsync(cancellationToken);
            sourceInput.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            targetInput.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }

        private void ReleaseInputs()
        {
            if (sourceInput is not null && GodotObject.IsInstanceValid(sourceInput)) sourceInput.Free();
            if (targetInput is not null && GodotObject.IsInstanceValid(targetInput)) targetInput.Free();
            sourceInput = targetInput = null;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("Backdrop is no longer preparable.");
            committed = true;
            if (backend.activeBackdrops.TryGetValue(surface.Id, out var old)) old.Complete();
            backend.activeBackdrops[surface.Id] = this;
            generation = host.BeginStageGeneration(surface.Id);
            if (host.IsCurrentStageGeneration(surface.Id, generation))
                host.ClearBackdropTransition(surface.Id);
            var animate = transition.DurationSeconds > 0 &&
                transition.Kind != DirectionTransitionId.Cut &&
                (source is not null || target is not null);
            if (animate)
            {
                if (sourceInput is not null)
                {
                    var inputState = (source ?? target)! with
                    {
                        Fit = BackdropFit.Stretch,
                        Tint = new DirectionColor(1, 1, 1, 1),
                    };
                    host.PublishBackdropTransition(surface.Id, inputState, sourceInput.GetTexture());
                }
                else if (source is not null && sourceTexture is not null)
                    host.PublishBackdropTransition(surface.Id, source, sourceTexture);
            }

            host.PublishBackdrop(surface.Id, target, targetTexture);
            if (!animate)
            {
                Complete();
                return;
            }

            var driver = drivers.GetRequired(transition.Kind);
            var context = new VisualTransitionContext(surface.BackdropTransition, transition,
                sourceInput?.GetTexture() ?? sourceTexture, targetInput?.GetTexture() ?? targetTexture,
                SourceIsPremultiplied: sourceInput is not null, TargetIsPremultiplied: targetInput is not null, IsActive: () => host.IsCurrentStageGeneration(surface.Id, generation));
            preparedTransition = driver.Prepare(context);
            if (transition.Kind != DirectionTransitionId.Fade) surface.Backdrop.Visible = false;
            clock = host.Animate(transition, easings.GetRequired(transition.Easing), p =>
            {
                if (host.IsCurrentStageGeneration(surface.Id, generation)) preparedTransition.ApplyProgress(p);
            }, OnFinished, exception => settlement.TrySetException(exception));
        }

        public ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed || rolledBack || disposed)
                throw new InvalidOperationException("Backdrop cannot finish in its current state.");
            Complete();
            return ValueTask.CompletedTask;
        }

        public void ConfirmCommit() => confirmation.TrySetResult(true);

        public void Rollback(DirectionCommitToken token)
        {
            if (rolledBack || disposed) return;
            rolledBack = true;
            confirmation.TrySetResult(false);
            clock?.Dispose();
            clock = null;
            preparedTransition?.Dispose();
            preparedTransition = null;
            ReleaseInputs();
            if (host.IsCurrentStageGeneration(surface.Id, generation))
            {
                host.PublishBackdrop(surface.Id, source, sourceTexture);
                host.ClearBackdropTransition(surface.Id);
            }
            settlement.TrySetCanceled();
        }

        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            if (!committed || rolledBack) confirmation.TrySetResult(false);
            Retire();
            if (!settlement.Task.IsCompleted) settlement.TrySetCanceled();
            clock?.Dispose();
            clock = null;
            preparedTransition?.Dispose();
            preparedTransition = null;
            ReleaseInputs();
            if (host.IsCurrentStageGeneration(surface.Id, generation))
                host.ClearBackdropTransition(surface.Id);
            return ValueTask.CompletedTask;
        }

        private void OnFinished() => Complete();

        private void Complete()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            clock?.Dispose();
            clock = null;
            if (host.IsCurrentStageGeneration(surface.Id, generation))
            {
                preparedTransition?.ApplyProgress(1.0f);
                host.PublishBackdrop(surface.Id, target, targetTexture);
                host.ClearBackdropTransition(surface.Id);
            }
            preparedTransition?.Dispose();
            preparedTransition = null;
            ReleaseInputs();
            Retire();
            _ = SettleAsync();
        }
        private void Retire()
        {
            if (backend.activeBackdrops.GetValueOrDefault(surface.Id) == this)
                backend.activeBackdrops.Remove(surface.Id);
        }

        private async Task SettleAsync()
        {
            try
            {
                if (target is null && await confirmation.Task && !rolledBack && host.IsCurrentStageGeneration(surface.Id, generation))
                    await host.ReleaseActorModifierTargetAsync(new ActorFxTarget(surface.Id, ActorFxSlot.Backdrop), surface.ModifierTarget);
                settlement.TrySetResult();
            }
            catch (Exception error) { settlement.TrySetException(error); }
        }
    }
}
