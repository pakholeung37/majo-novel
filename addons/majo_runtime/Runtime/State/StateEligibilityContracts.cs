#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.State;

public enum StateOperationKind
{
    Save,
    Restore,
    Rollback,
    HotReload,
}

[Flags]
public enum StateOperationMask
{
    None = 0,
    Save = 1,
    Restore = 2,
    Rollback = 4,
    HotReload = 8,
    All = Save | Restore | Rollback | HotReload,
}

public enum StateEligibilityReasonCode
{
    ScriptUnsafePhase,
    StateTransactionActive,
    ActiveCommand,
    ActiveOperation,
    ModalInput,
    ExternalBarrier,
    UnsupportedRollbackContributor,
}

public sealed record StateEligibilityReason(
    StateEligibilityReasonCode Code,
    string Diagnostic,
    string? PlayerMessage = null);

public sealed record StateEligibilityResult(
    StateOperationKind Operation,
    IReadOnlyList<StateEligibilityReason> Reasons)
{
    public bool IsAllowed => Reasons.Count == 0;
}

public interface IStateBarrierHandle : IDisposable
{
    string ReasonCode { get; }
    StateOperationMask Operations { get; }
    bool IsDisposed { get; }
}

public interface IStateEligibility : IRuntimeService
{
    StateEligibilityResult Check(StateOperationKind operation);

    IStateBarrierHandle AcquireBarrier(
        object owner,
        string reasonCode,
        StateOperationMask operations = StateOperationMask.All,
        string? diagnostic = null,
        string? playerMessage = null);
}
