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
    DirectionTransitionSpec Spec);

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
    DirectionCompletionBarrier Completion);

public readonly record struct DirectionCommitToken(
    DirectionTransactionId TransactionId,
    long Revision);
