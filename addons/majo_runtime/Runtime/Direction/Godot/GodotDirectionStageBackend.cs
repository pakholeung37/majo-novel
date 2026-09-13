#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations.Godot;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Direction.Godot;

internal sealed partial class GodotDirectionStageBackend : IDirectionStageBackend, IRuntimeService
{
    internal long LocaleRevision { get; private set; }
    internal void InvalidateLocale() => LocaleRevision++;
    private readonly Dictionary<StageId, PreparedBackdrop> activeBackdrops = new();
    private readonly GodotDirectionRenderHost host;
    private readonly Dictionary<StageId, GodotDirectionBackdropBinding> backgrounds = new();
    private readonly Func<ActorMountRoots, IActorManager>? createActorManager;
    private readonly ITransitionDriverRegistry drivers;
    private readonly IEasingRegistry easings;
    private readonly Dictionary<StageId, GodotDirectionActorTreeBackend> actors = new();

    public GodotDirectionStageBackend(
        GodotDirectionRenderHost host,
        Func<ActorMountRoots, IActorManager>? createActorManager = null,
        ITransitionDriverRegistry? drivers = null,
        IEasingRegistry? easings = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
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
        foreach (var transition in activeBackdrops.Values.ToArray())
        {
            try { await transition.FinishAsync(CancellationToken.None); }
            catch (Exception error) { (failures ??= new()).Add(error); }
            try { await transition.DisposeAsync(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        activeBackdrops.Clear();
        foreach (var pair in backgrounds.ToArray())
        {
            try { if (host.HasStage(pair.Key)) host.PublishBackdrop(pair.Key, null, null); }
            catch (Exception error) { (failures ??= new()).Add(error); }
            try { await pair.Value.DisposeAsync(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        backgrounds.Clear();
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
        foreach (var transition in activeBackdrops.Values.ToArray())
        {
            try { await transition.FinishAsync(CancellationToken.None); }
            catch (Exception error) { (failures ??= new()).Add(error); }
            try { await transition.DisposeAsync(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        activeBackdrops.Clear();
        foreach (var pair in backgrounds.ToArray())
        {
            try { if (host.HasStage(pair.Key)) host.PublishBackdrop(pair.Key, null, null); }
            catch (Exception error) { (failures ??= new()).Add(error); }
            try { await pair.Value.DisposeAsync(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        backgrounds.Clear();
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

        var backdropChanged = (plan.IsStateReplacement && target.Backdrop is not null) || !Equals(before?.Backdrop, target?.Backdrop) || plan.Transitions.Any(transition =>
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
        if (activeBackdrops.TryGetValue(stageId, out var previous))
        {
            await previous.FinishAsync(cancellationToken);
            await previous.Settlement;
            await previous.DisposeAsync();
        }
        var surface = host.GetStage(stageId);
        var transition = GodotDirectionRendering.ResolveTransition(plan,
            path => path == $"stage/{stageId.Value}/backdrop");
        GodotDirectionRendering.ValidateTransition(transition, TransitionDomains.Stage, drivers, easings);
        backgrounds.TryGetValue(stageId, out var sourceBinding);
        var prepared = new PreparedBackdrop(this, surface, sourceBinding, target?.Backdrop, transition, plan.IsStateReplacement);
        try { await prepared.PrepareAsync(cancellationToken); return prepared; }
        catch (Exception failure)
        {
            try { await prepared.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
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
        if (activeBackdrops.TryGetValue(stageId, out var transition))
        {
            try { await transition.FinishAsync(CancellationToken.None); }
            catch (Exception error) { failure = error; }
            try { await transition.DisposeAsync(); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }
        if (backgrounds.Remove(stageId, out var background))
        {
            try { if (host.HasStage(stageId)) host.PublishBackdrop(stageId, null, null); await background.DisposeAsync(); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }
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
        private bool confirmed;
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

        public void ConfirmCommit() => confirmed = true;

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
            if (committed && confirmed) await backend.ReleasePreparedStageAsync(stageId);
        }
    }

    private sealed class PreparedStageLifecycle : IPreparedDirectionComponent
    {
        private readonly GodotDirectionStageBackend backend;
        private readonly StageId stageId;
        private readonly IPreparedDirectionComponent component;
        private bool committed;
        private bool confirmed;
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

        public void ConfirmCommit() { component.ConfirmCommit(); confirmed = true; }

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
            if (!committed || !confirmed)
            {
                try { await backend.ReleasePreparedStageAsync(stageId); }
                catch (Exception exception) { (failures ??= new()).Add(exception); }
            }
            if (failures is not null)
                throw new AggregateException("Stage component cleanup failed.", failures);
        }
    }

}
