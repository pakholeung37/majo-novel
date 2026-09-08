#nullable enable

using System;
using System.Collections.Generic;

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
    internal Dictionary<ViewId, ViewState> CommitViews { get; } = new();
    public IReadOnlyList<ViewPosePatch> ViewIntents { get; init; } = Array.Empty<ViewPosePatch>();

    internal Dictionary<(StageId Stage, ActorId Actor), ActorState> CommitActors { get; } = new();

    internal DirectionState ResolveCommitState()
    {
        if (CommitActors.Count == 0 && CommitViews.Count == 0) return TargetState;
        var views = System.Linq.Enumerable.ToDictionary(TargetState.Views, pair => pair.Key, pair => pair.Value);
        foreach (var (id, view) in CommitViews) views[id] = view;
        CommitViews.Clear();
        var stages = System.Linq.Enumerable.ToDictionary(TargetState.Stages, pair => pair.Key, pair => pair.Value);
        foreach (var (key, actor) in CommitActors)
        {
            var stage = stages[key.Stage];
            var actors = System.Linq.Enumerable.ToDictionary(stage.Actors, pair => pair.Key, pair => pair.Value);
            actors[key.Actor] = actor;
            stages[key.Stage] = stage with { Actors = DirectionCollections.Freeze(actors) };
        }
        CommitActors.Clear();
        return TargetState with { Stages = DirectionCollections.Freeze(stages), Views = DirectionCollections.Freeze(views) };
    }

    public IReadOnlyList<IDurableDirectionPatch> ActorIntents { get; init; } = Array.Empty<IDurableDirectionPatch>();

}
