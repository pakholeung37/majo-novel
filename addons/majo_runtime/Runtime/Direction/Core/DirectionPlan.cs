#nullable enable

using System.Collections.Generic;

namespace Majo.Runtime.Direction.Core;

public enum DirectionResourceKind
{
    Backdrop,
    ActorAppearance,
    Cg,
    Layout,
}

public sealed record DirectionResourceClaim(DirectionResourceKind Kind, string SemanticId);

public sealed record DirectionTransition(
    IReadOnlyList<string> StablePaths,
    DirectionTransitionSpec Spec,
    DirectionSourceLocation? SourceLocation = null);

/// <summary>
/// Describes a view pose that must be published as the cut baseline before a
/// later pose transition in the same Direction transaction starts.
/// </summary>
public sealed record DirectionViewTransitionSequence(
    ViewId ViewId,
    ViewState StartState,
    DirectionTransitionSpec Transition);

public enum DirectionCompletionBarrier
{
    Commit,
    Settle,
}

public sealed record DirectionPlan(
    DirectionTransactionId Id,
    DirectionState BeforeState,
    DirectionState TargetState,
    IReadOnlyList<DirectionTransition> Transitions,
    IReadOnlyList<ITransientDirectionCue> Cues,
    IReadOnlyList<DirectionResourceClaim> ResourceClaims,
    DirectionCompletionBarrier Completion,
    IReadOnlyList<DirectionViewTransitionSequence>? ViewSequences = null);

public readonly record struct DirectionCommitToken(
    DirectionTransactionId TransactionId,
    long Revision);
