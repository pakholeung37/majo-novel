#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Rendering;

public sealed class CompositeDirectionPreparationBackend : IDirectionPreparationBackend
{
    private readonly IDirectionStageBackend stages;
    private readonly IDirectionViewBackend views;
    private readonly IDirectionScreenBackend screen;
    private readonly IDirectionEffectBackend effects;

    public CompositeDirectionPreparationBackend(
        IDirectionStageBackend stages,
        IDirectionViewBackend views,
        IDirectionScreenBackend screen,
        IDirectionEffectBackend effects)
    {
        this.stages = stages ?? throw new ArgumentNullException(nameof(stages));
        this.views = views ?? throw new ArgumentNullException(nameof(views));
        this.screen = screen ?? throw new ArgumentNullException(nameof(screen));
        this.effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    public async Task<IPreparedDirectionTransaction> PrepareAsync(
        DirectionPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var prepared = new List<IPreparedDirectionComponent>();
        try
        {
            // Views may read textures from stages created by the same plan, and
            // the screen may read view viewports. Prepare those dependency
            // layers in order; otherwise valid plans could fail nondeterministically
            // depending on which task happened to run first.
            await AddPreparedBatchAsync(
                BuildStagePreparations(plan, cancellationToken), prepared);
            await AddPreparedBatchAsync(
                BuildViewPreparations(plan, cancellationToken), prepared);
            await AddPreparedBatchAsync(
                BuildScreenAndEffectPreparations(plan, cancellationToken), prepared);
        }
        catch (Exception preparationException)
        {
            List<Exception>? cleanupFailures = null;
            foreach (var component in prepared.AsEnumerable().Reverse())
            {
                try { await component.DisposeAsync(); }
                catch (Exception exception)
                {
                    (cleanupFailures ??= new List<Exception>()).Add(exception);
                }
            }
            if (cleanupFailures is not null)
                throw new AggregateException(
                    "Direction preparation failed and one or more components could not be disposed.",
                    new[] { preparationException }.Concat(cleanupFailures));
            throw;
        }

        if (prepared.Count == 0)
            return new PreparedComposite(Array.Empty<IPreparedDirectionComponent>());

        return new PreparedComposite(prepared.ToArray());
    }

    private static async Task AddPreparedBatchAsync(
        IReadOnlyList<Task<IPreparedDirectionComponent>> preparations,
        ICollection<IPreparedDirectionComponent> prepared)
    {
        if (preparations.Count == 0) return;
        try
        {
            var components = await Task.WhenAll(preparations);
            if (components.Any(component => component is null))
                throw new DirectionException(
                    "Direction component backend returned a null prepared component.");
            foreach (var component in components)
                prepared.Add(component);
        }
        catch (Exception preparationException)
        {
            List<Exception>? cleanupFailures = null;
            foreach (var preparation in preparations)
            {
                if (preparation.Status == TaskStatus.RanToCompletion)
                {
                    // A successful task from a failed batch is not in the
                    // aggregate yet, so dispose it here. The caller disposes
                    // earlier completed batches.
                    try
                    {
                        if (preparation.Result is not null)
                            await preparation.Result.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        (cleanupFailures ??= new List<Exception>()).Add(exception);
                    }
                }
            }
            if (cleanupFailures is not null)
                throw new AggregateException(
                    "Direction preparation failed and one or more components could not be disposed.",
                    new[] { preparationException }.Concat(cleanupFailures));
            throw;
        }
    }

    private List<Task<IPreparedDirectionComponent>> BuildStagePreparations(
        DirectionPlan plan,
        CancellationToken cancellationToken)
    {
        var preparations = new List<Task<IPreparedDirectionComponent>>();
        foreach (var stageId in plan.BeforeState.Stages.Keys
            .Union(plan.TargetState.Stages.Keys)
            .OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            plan.BeforeState.Stages.TryGetValue(stageId, out var before);
            plan.TargetState.Stages.TryGetValue(stageId, out var target);
            if (!Equals(before, target))
            {
                preparations.Add(CapturePreparation(() =>
                    stages.PrepareAsync(plan, stageId, before, target, cancellationToken)));
            }
        }

        return preparations;
    }

    private List<Task<IPreparedDirectionComponent>> BuildViewPreparations(
        DirectionPlan plan,
        CancellationToken cancellationToken)
    {
        var preparations = new List<Task<IPreparedDirectionComponent>>();
        foreach (var viewId in plan.BeforeState.Views.Keys
            .Union(plan.TargetState.Views.Keys)
            .OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            plan.BeforeState.Views.TryGetValue(viewId, out var before);
            plan.TargetState.Views.TryGetValue(viewId, out var target);
            if (!Equals(before, target))
            {
                preparations.Add(CapturePreparation(() =>
                    views.PrepareAsync(plan, viewId, before, target, cancellationToken)));
            }
        }

        return preparations;
    }

    private List<Task<IPreparedDirectionComponent>> BuildScreenAndEffectPreparations(
        DirectionPlan plan,
        CancellationToken cancellationToken)
    {
        var preparations = new List<Task<IPreparedDirectionComponent>>();
        if (!Equals(plan.BeforeState.Screen, plan.TargetState.Screen))
        {
            preparations.Add(CapturePreparation(() => screen.PrepareAsync(
                    plan,
                    plan.BeforeState.Screen,
                    plan.TargetState.Screen,
                    cancellationToken)));
        }

        if (!Equals(plan.BeforeState.Effects, plan.TargetState.Effects))
        {
            preparations.Add(CapturePreparation(() => effects.PrepareAsync(
                    plan,
                    plan.BeforeState.Effects,
                    plan.TargetState.Effects,
                    cancellationToken)));
        }

        return preparations;
    }

    private static Task<IPreparedDirectionComponent> CapturePreparation(
        Func<Task<IPreparedDirectionComponent>> prepare)
    {
        try
        {
            return prepare() ?? Task.FromException<IPreparedDirectionComponent>(
                new DirectionException("Direction component backend returned no preparation task."));
        }
        catch (Exception exception)
        {
            return Task.FromException<IPreparedDirectionComponent>(exception);
        }
    }

    private sealed class PreparedComposite : IPreparedDirectionTransaction
    {
        private readonly IReadOnlyList<IPreparedDirectionComponent> components;
        private readonly TaskCompletionSource emptySettlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object sync = new();
        private readonly List<IPreparedDirectionComponent> committedComponents = new();
        private CompositeState state;

        public PreparedComposite(IReadOnlyList<IPreparedDirectionComponent> components)
        {
            this.components = components;
        }

        public Task Settlement => components.Count == 0
            ? emptySettlement.Task
            : Task.WhenAll(components.Select(component => component.Settlement));

        public void Commit(DirectionCommitToken token)
        {
            lock (sync)
            {
                if (state != CompositeState.Prepared)
                    throw new InvalidOperationException(
                        $"Direction transaction cannot commit from state '{state}'.");
                state = CompositeState.Committing;
            }

            try
            {
                foreach (var component in components)
                {
                    // Add before invoking Commit. A component is allowed to publish
                    // part of its state and then throw; its rollback must still run.
                    lock (sync) committedComponents.Add(component);
                    component.Commit(token);
                }
                lock (sync) state = CompositeState.Committed;
                if (components.Count == 0) emptySettlement.TrySetResult();
            }
            catch (Exception exception)
            {
                try
                {
                    Rollback(token);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Direction composite commit failed and compensation also failed.",
                        exception,
                        rollbackException);
                }
                throw;
            }
        }

        public void Rollback(DirectionCommitToken token)
        {
            lock (sync)
            {
                if (state is CompositeState.RolledBack or CompositeState.Disposed)
                    return;
                state = CompositeState.RollingBack;
            }

            List<Exception>? failures = null;
            IPreparedDirectionComponent[] committed;
            lock (sync) committed = committedComponents.AsEnumerable().Reverse().ToArray();
            foreach (var component in committed)
            {
                try
                {
                    component.Rollback(token);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
            lock (sync)
            {
                committedComponents.Clear();
                state = CompositeState.RolledBack;
            }
            if (failures is not null)
                throw new AggregateException("One or more Direction commit compensations failed.", failures);
        }

        public async ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (state is not CompositeState.Committed)
                    throw new InvalidOperationException(
                        $"Direction transaction cannot finish from state '{state}'.");
            }

            List<Exception>? failures = null;
            foreach (var component in components)
            {
                try
                {
                    await component.FinishAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }

            if (failures is not null)
                throw new AggregateException("One or more Direction transitions failed to finish.", failures);
        }

        public async ValueTask DisposeAsync()
        {
            lock (sync)
            {
                if (state == CompositeState.Disposed) return;
                state = CompositeState.Disposed;
            }

            List<Exception>? failures = null;
            foreach (var component in components.Reverse())
            {
                try
                {
                    await component.DisposeAsync();
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }

            if (failures is not null)
                throw new AggregateException("One or more Direction prepared components failed to dispose.", failures);
        }

        private enum CompositeState
        {
            Prepared,
            Committing,
            Committed,
            RollingBack,
            RolledBack,
            Disposed,
        }
    }
}
