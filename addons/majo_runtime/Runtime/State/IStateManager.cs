#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.State;

public sealed class StateRestorePlan
{
    public StateRestorePlan(
        StateSnapshot snapshot,
        StateScope scope,
        StateOperationKind operation,
        IReadOnlyDictionary<string, StateRecord> records,
        IReadOnlyList<string>? migrationChain = null)
        : this(new object(), snapshot, scope, operation, records,
            migrationChain ?? Array.Empty<string>())
    {
    }

    internal StateRestorePlan(
        object owner,
        StateSnapshot snapshot,
        StateScope scope,
        StateOperationKind operation,
        IReadOnlyDictionary<string, StateRecord> records,
        IReadOnlyList<string> migrationChain)
    {
        Owner = owner;
        Snapshot = snapshot;
        Scope = scope;
        Operation = operation;
        Records = records;
        MigrationChain = migrationChain;
    }

    internal object Owner { get; }
    public StateSnapshot Snapshot { get; }
    public StateScope Scope { get; }
    public StateOperationKind Operation { get; }
    public IReadOnlyDictionary<string, StateRecord> Records { get; }
    public IReadOnlyList<string> MigrationChain { get; }
}

public interface IStateRestoreTransaction
{
    Task<StateRestorePlan> PreflightAsync(
        StateSnapshot snapshot,
        StateScope scope,
        StateOperationKind operation = StateOperationKind.Restore,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        StateRestorePlan plan,
        CancellationToken cancellationToken = default);
}

public interface IStateManager : IRuntimeService
{
    event Action<StateStatusChanged>? StatusChanged;

    event Action<StateSnapshot>? Captured;

    event Action<StateSnapshot>? Restored;

    event Action<Exception>? Failed;

    StateManagerStatus Status { get; }

    StateSnapshot? CurrentSnapshot { get; }

    IStateRestoreTransaction RestoreTransaction { get; }

    Task<StateSnapshot> CaptureAsync(
        StateScope scope,
        CancellationToken cancellationToken = default);

    Task<StateSnapshot> CaptureGameAsync(
        CancellationToken cancellationToken = default);

    StateEnvelope CreateEnvelope(
        StateSnapshot snapshot,
        StateScope scope,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        IReadOnlyDictionary<string, string>? displayMetadata = null);

    Task ResetScopeAsync(
        StateScope scope,
        CancellationToken cancellationToken = default);

    Task RestoreGameAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

public sealed record CommittedLineBoundary(
    Majo.Runtime.Script.LineBoundaryContext Context,
    StateSnapshot Snapshot);

public interface IStateBoundarySource
{
    event Action<CommittedLineBoundary>? BoundaryCommitted;
    CommittedLineBoundary? CurrentBoundary { get; }
}
