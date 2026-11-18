#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Majo.Runtime.Direction.Core;

public sealed record DirectionTransition(
    IReadOnlyList<string> StablePaths,
    DirectionTransitionSpec Spec,
    DirectionSourceLocation? SourceLocation = null);

public sealed record DirectionPlan(
    DirectionTransactionId Id,
    DirectionState BeforeState,
    DirectionState TargetState,
    IReadOnlyList<DirectionTransition> Transitions,
    IReadOnlyList<ITransientDirectionCue> Cues)
{
    /// <summary>Restore must reconcile actual output even when its logical target is unchanged.</summary>
    public bool IsStateReplacement { get; init; }

    private readonly Dictionary<ViewId, ViewState> commitViews = new();
    public IReadOnlyList<ViewPosePatch> ViewIntents { get; init; } = Array.Empty<ViewPosePatch>();

    private readonly Dictionary<(StageId Stage, ActorId Actor), ActorState> commitActors = new();

    /// <summary>Records the prepared view state sampled by a backend during commit.</summary>
    public void RecordViewCommit(ViewState view)
    {
        ArgumentNullException.ThrowIfNull(view);
        commitViews[view.Id] = view;
    }

    /// <summary>Records prepared actor state; this does not mutate the immutable planning target.</summary>
    public void RecordActorCommit(StageId stageId, ActorState actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        commitActors[(stageId, actor.Id)] = actor;
    }

    public bool TryGetActorCommit(StageId stageId, ActorId actorId, [NotNullWhen(true)] out ActorState? actor) =>
        commitActors.TryGetValue((stageId, actorId), out actor);

    /// <summary>Merges and consumes the samples after prepared components have committed.</summary>
    public DirectionState ResolveCommitState()
    {
        if (commitActors.Count == 0 && commitViews.Count == 0) return TargetState;
        var views = System.Linq.Enumerable.ToDictionary(TargetState.Views, pair => pair.Key, pair => pair.Value);
        foreach (var (id, view) in commitViews) views[id] = view;
        commitViews.Clear();
        var stages = System.Linq.Enumerable.ToDictionary(TargetState.Stages, pair => pair.Key, pair => pair.Value);
        foreach (var (key, actor) in commitActors)
        {
            var stage = stages[key.Stage];
            var actors = System.Linq.Enumerable.ToDictionary(stage.Actors, pair => pair.Key, pair => pair.Value);
            actors[key.Actor] = actor;
            stages[key.Stage] = stage with { Actors = DirectionCollections.Freeze(actors) };
        }
        commitActors.Clear();
        return TargetState with { Stages = DirectionCollections.Freeze(stages), Views = DirectionCollections.Freeze(views) };
    }

    public IReadOnlyList<IDurableDirectionPatch> ActorIntents { get; init; } = Array.Empty<IDurableDirectionPatch>();

}
