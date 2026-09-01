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
        var preparations = BuildPreparations(plan, cancellationToken);
        if (preparations.Count == 0)
            return new PreparedComposite(Array.Empty<IPreparedDirectionComponent>());

        try
        {
            var components = await Task.WhenAll(preparations);
            return new PreparedComposite(components);
        }
        catch
        {
            foreach (var preparation in preparations)
            {
                if (preparation.Status == TaskStatus.RanToCompletion)
                    await preparation.Result.DisposeAsync();
            }
            throw;
        }
    }

    private List<Task<IPreparedDirectionComponent>> BuildPreparations(
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
        private int committed;
        private int disposed;

        public PreparedComposite(IReadOnlyList<IPreparedDirectionComponent> components)
        {
            this.components = components;
        }

        public Task Settlement => components.Count == 0
            ? emptySettlement.Task
            : Task.WhenAll(components.Select(component => component.Settlement));

        public void Commit(DirectionCommitToken token)
        {
            if (Interlocked.Exchange(ref committed, 1) != 0)
                throw new InvalidOperationException("Direction transaction was already committed.");

            foreach (var component in components) component.Commit(token);
            if (components.Count == 0) emptySettlement.TrySetResult();
        }

        public async ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref committed) == 0)
                throw new InvalidOperationException("Direction transaction has not committed.");
            foreach (var component in components)
                await component.FinishAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            foreach (var component in components.Reverse())
                await component.DisposeAsync();
        }
    }
}
