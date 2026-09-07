#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Direction.Core;

public sealed record DirectionTransition(
    IReadOnlyList<string> StablePaths,
    DirectionTransitionSpec Spec,
    DirectionSourceLocation? SourceLocation = null);

public sealed record DirectionViewPoseIntent(
    ViewId ViewId,
    ViewState StartState,
    DirectionTransitionSpec Transition);

public sealed record DirectionPlan(
    DirectionTransactionId Id,
    DirectionState BeforeState,
    DirectionState TargetState,
    IReadOnlyList<DirectionTransition> Transitions,
    IReadOnlyList<ITransientDirectionCue> Cues,
    IReadOnlyList<DirectionViewPoseIntent>? ViewPoseIntents = null)
{
    public IReadOnlyList<DirectionViewPoseIntent> ViewPoseIntents { get; init; } =
        Array.AsReadOnly(ViewPoseIntents is null ? Array.Empty<DirectionViewPoseIntent>() :
            System.Linq.Enumerable.ToArray(ViewPoseIntents));
}
