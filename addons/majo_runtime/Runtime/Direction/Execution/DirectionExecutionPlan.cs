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
    ObjectDefinition,
    Layout,
}

public sealed record DirectionResourceClaim(DirectionResourceKind Kind, string SemanticId);

public enum DirectionCompletionBarrier
{
    Commit,
    Settle,
}

public readonly record struct DirectionCommitToken(
    DirectionTransactionId TransactionId,
    long Revision);

public sealed record DirectionExecutionPlan(
    DirectionPlan DomainPlan,
    IReadOnlyList<DirectionResourceClaim> ResourceClaims,
    DirectionCompletionBarrier Completion)
{
    public DirectionTransactionId Id => DomainPlan.Id;
    public DirectionState BeforeState => DomainPlan.BeforeState;
    public DirectionState TargetState => DomainPlan.TargetState;
    public IReadOnlyList<DirectionTransition> Transitions => DomainPlan.Transitions;
    public IReadOnlyList<ITransientDirectionCue> Cues => DomainPlan.Cues;

    public static implicit operator DirectionPlan(DirectionExecutionPlan executionPlan) =>
        executionPlan.DomainPlan;
}
