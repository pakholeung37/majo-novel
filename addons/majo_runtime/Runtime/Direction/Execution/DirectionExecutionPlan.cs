#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Execution;

public enum DirectionResourceKind
{
    Backdrop,
    ActorAppearance,
    Layout,
}

public sealed record DirectionResourceClaim(DirectionResourceKind Kind, string SemanticId);

public enum DirectionCompletionBarrier
{
    Commit,
    Settle,
}

/// <summary>
/// Describes a view pose that must be published as the cut baseline before a
/// later pose transition in the same Direction transaction starts.
/// </summary>
public sealed record DirectionViewTransitionSequence(
    ViewId ViewId,
    ViewState StartState,
    DirectionTransitionSpec Transition);

public readonly record struct DirectionCommitToken(
    DirectionTransactionId TransactionId,
    long Revision);

public sealed record DirectionExecutionPlan(
    DirectionPlan DomainPlan,
    IReadOnlyList<DirectionResourceClaim> ResourceClaims,
    DirectionCompletionBarrier Completion,
    IReadOnlyList<DirectionViewTransitionSequence>? ViewSequences = null)
{
    public IReadOnlyList<DirectionViewTransitionSequence> ViewSequences { get; init; } =
        ViewSequences ?? DomainPlan.ViewPoseIntents.Select(intent =>
            new DirectionViewTransitionSequence(intent.ViewId, intent.StartState, intent.Transition)).ToArray();
    public DirectionTransactionId Id => DomainPlan.Id;
    public DirectionState BeforeState => DomainPlan.BeforeState;
    public DirectionState TargetState => DomainPlan.TargetState;
    public IReadOnlyList<DirectionTransition> Transitions => DomainPlan.Transitions;
    public IReadOnlyList<ITransientDirectionCue> Cues => DomainPlan.Cues;

    public static implicit operator DirectionPlan(DirectionExecutionPlan executionPlan) =>
        executionPlan.DomainPlan;
}
