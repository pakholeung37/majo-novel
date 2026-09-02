#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;

namespace Majo.Runtime.State;

public sealed class StateManager : IStateManager, IStateRestoreTransaction, IDisposable
{
    private readonly SemaphoreSlim transactions = new(1, 1);
    private readonly IReadOnlyList<IStateContributor> contributors;
    private readonly IDialoguePlayer dialogue;
    private readonly IInputManager input;
    private readonly IOperationScheduler operations;
    private readonly IStateEligibility eligibility;
    private readonly StateMigrationRegistry migrations;
    private readonly IRuntimeLogger logger;
    private readonly object restorePlanOwner = new();
    private IInputHandle? faultInputMuter;
    private bool isInitialized;

    public StateManager(
        IReadOnlyList<IStateContributor> contributors,
        IDialoguePlayer dialogue,
        IInputManager input,
        IOperationScheduler operations,
        IStateEligibility? eligibility = null,
        StateMigrationRegistry? migrations = null,
        IRuntimeLogger? logger = null)
    {
        this.contributors = contributors ?? throw new ArgumentNullException(nameof(contributors));
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.eligibility = eligibility ?? new PermissiveStateEligibility();
        this.migrations = migrations ?? new StateMigrationRegistry();
        this.logger = logger ?? NullRuntimeLogger.Instance;
        if (this.eligibility is StateEligibility concreteEligibility)
        {
            concreteEligibility.Attach(() => Status, contributors);
        }
    }

    public event Action<StateStatusChanged>? StatusChanged;
    public event Action<StateSnapshot>? Captured;
    public event Action<StateSnapshot>? Restored;
    public event Action<Exception>? Failed;

    public StateManagerStatus Status { get; private set; } = StateManagerStatus.Idle;
    public StateSnapshot? CurrentSnapshot { get; private set; }
    public IStateRestoreTransaction RestoreTransaction => this;
    public IReadOnlyList<string> RegisteredContributorKeys =>
        contributors.Select(value => value.Key).ToArray();
    public string? LastFailureMessage { get; private set; }
    public IReadOnlyList<string> LastMigrationChain { get; private set; } = Array.Empty<string>();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        SetStatus(StateManagerStatus.Idle);
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return;
        }

        await transactions.WaitAsync(cancellationToken);
        try
        {
            foreach (var contributor in contributors.Where(value =>
                         value.Scope == StateScope.Session).Reverse())
            {
                await contributor.ResetAsync(cancellationToken);
            }

            CurrentSnapshot = null;
            LastFailureMessage = null;
            LastMigrationChain = Array.Empty<string>();
            ReleaseFaultMuter();
            SetStatus(StateManagerStatus.Idle);
        }
        finally
        {
            transactions.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return;
        }

        await transactions.WaitAsync(cancellationToken);
        try
        {
            CurrentSnapshot = null;
            LastMigrationChain = Array.Empty<string>();
            ReleaseFaultMuter();
            isInitialized = false;
            SetStatus(StateManagerStatus.Idle);
        }
        finally
        {
            transactions.Release();
        }
    }

    public Task<StateSnapshot> CaptureGameAsync(
        CancellationToken cancellationToken = default) =>
        CaptureAsync(StateScope.Game, cancellationToken);

    public StateEnvelope CreateEnvelope(
        StateSnapshot snapshot,
        StateScope scope,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        IReadOnlyDictionary<string, string>? displayMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var durable = PersistentContributors(scope).ToDictionary(
            value => value.Key,
            StringComparer.Ordinal);
        var metadata = snapshot.Records.Values.Select(record =>
        {
            if (!durable.TryGetValue(record.Key, out var contributor) ||
                record.SchemaVersion != contributor.SchemaVersion)
            {
                throw new StateCompatibilityException(
                    $"Snapshot record '{record.Key}' does not match a durable contributor.");
            }

            return new StateEnvelopeRecordMetadata(
                record.Key,
                record.SchemaVersion,
                contributor.Capability);
        }).ToArray();

        StateScriptSummary? script = null;
        if (scope == StateScope.Game && snapshot.Records.TryGetValue(
                BuiltInStateKeys.ScriptPlayback,
                out var scriptRecord))
        {
            try
            {
                var payload = scriptRecord.Payload;
                script = new StateScriptSummary(
                    payload.GetProperty("projectId").GetString()!,
                    payload.GetProperty("nodeName").GetString()!,
                    payload.GetProperty("lineId").GetString()!,
                    payload.GetProperty("programFingerprint").GetString()!);
            }
            catch (Exception exception) when (
                exception is KeyNotFoundException or InvalidOperationException)
            {
                throw new StateSnapshotException(
                    "Script state record cannot provide envelope summary metadata.",
                    exception);
            }
        }

        return new StateEnvelope(
            StateEnvelope.CurrentFormatVersion,
            runtimeVersion,
            gameBuildId,
            storageNamespace,
            DateTimeOffset.UtcNow,
            scope,
            snapshot,
            metadata,
            script,
            displayMetadata);
    }

    public async Task<StateSnapshot> CaptureAsync(
        StateScope scope,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (scope == StateScope.Session)
        {
            throw new ArgumentException("Session state is transient and cannot be persisted.", nameof(scope));
        }

        await transactions.WaitAsync(cancellationToken);
        try
        {
            EnsureReadyForTransaction("capture");
            if (scope == StateScope.Game)
            {
                EnsureEligible(StateOperationKind.Save);
            }

            SetStatus(StateManagerStatus.Capturing);
            logger.Info("state", "state.capture_started", $"State capture started for scope '{scope}'.");
            var records = new List<StateRecord>();
            foreach (var contributor in PersistentContributors(scope))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = await contributor.CaptureAsync(cancellationToken);
                ValidateRecord(contributor, record);
                records.Add(record);
            }

            var snapshot = new StateSnapshot(
                StateSnapshot.CurrentFormatVersion,
                DateTimeOffset.UtcNow,
                records);
            CurrentSnapshot = snapshot;
            LastFailureMessage = null;
            LastMigrationChain = Array.Empty<string>();
            SetStatus(StateManagerStatus.Idle);
            logger.Info(
                "state",
                "state.capture_completed",
                $"State capture completed with {records.Count} records for scope '{scope}'.");
            Captured?.Invoke(snapshot);
            return snapshot;
        }
        catch (StateEligibilityException exception)
        {
            LastFailureMessage = exception.Message;
            SetStatus(StateManagerStatus.Idle);
            logger.Warning("state", "state.capture_rejected", exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            SetStatus(StateManagerStatus.Faulted);
            LastFailureMessage = exception.Message;
            logger.Error(
                "state",
                "state.capture_failed",
                $"State capture failed: {exception.Message}",
                exception);
            Failed?.Invoke(exception);
            throw;
        }
        finally
        {
            transactions.Release();
        }
    }

    public async Task RestoreGameAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var plan = await PreflightAsync(
            snapshot,
            StateScope.Game,
            StateOperationKind.Restore,
            cancellationToken);
        await ExecuteAsync(plan, cancellationToken);
    }

    public async Task ResetScopeAsync(
        StateScope scope,
        CancellationToken cancellationToken = default)
    {
        if (scope == StateScope.Session)
        {
            throw new ArgumentException(
                "Session state is reset through the game-session coordinator.",
                nameof(scope));
        }

        EnsureInitialized();
        await transactions.WaitAsync(cancellationToken);
        IInputHandle? inputMuter = null;
        var mutationStarted = false;
        try
        {
            EnsureReadyForTransaction("reset scope");
            if (scope == StateScope.Game)
            {
                EnsureEligible(StateOperationKind.Restore);
            }
            SetStatus(StateManagerStatus.Restoring);
            inputMuter = input.AcquireMuter(this, new InputMuteOptions(name: "state-reset"));
            mutationStarted = true;
            await dialogue.StopAsync(cancellationToken);
            await CancelActiveOperationsAsync(cancellationToken);
            foreach (var contributor in PersistentContributors(scope).Reverse())
            {
                await contributor.ResetAsync(cancellationToken);
            }
            CurrentSnapshot = null;
            LastFailureMessage = null;
            LastMigrationChain = Array.Empty<string>();
            SetStatus(StateManagerStatus.Idle);
            logger.Info(
                "state",
                "state.scope_reset_completed",
                $"State scope '{scope}' reset completed.");
        }
        catch (StateEligibilityException exception)
        {
            LastFailureMessage = exception.Message;
            SetStatus(StateManagerStatus.Idle);
            throw;
        }
        catch (Exception exception)
        {
            if (mutationStarted && inputMuter is not null)
            {
                faultInputMuter = inputMuter;
                inputMuter = null;
            }
            SetStatus(StateManagerStatus.Faulted);
            LastFailureMessage = exception.Message;
            Failed?.Invoke(exception);
            throw;
        }
        finally
        {
            inputMuter?.Dispose();
            transactions.Release();
        }
    }

    public async Task<StateRestorePlan> PreflightAsync(
        StateSnapshot snapshot,
        StateScope scope,
        StateOperationKind operation = StateOperationKind.Restore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureInitialized();
        await transactions.WaitAsync(cancellationToken);
        IReadOnlyList<string> attemptedMigrationChain = Array.Empty<string>();
        try
        {
            EnsureReadyForTransaction("preflight restore");
            EnsureEligible(operation);
            ValidateSnapshotFormat(snapshot);
            logger.Info(
                "state",
                "state.restore_preflight_started",
                $"State restore preflight started for scope '{scope}'.");
            var resolved = migrations.ResolveRecords(snapshot, contributors, scope);
            attemptedMigrationChain = resolved.Chain;
            foreach (var contributor in PersistentContributors(scope))
            {
                if (!resolved.Value.TryGetValue(contributor.Key, out var record))
                {
                    continue;
                }

                ValidateRecord(contributor, record);
                await contributor.PreflightAsync(record, cancellationToken);
            }

            LastFailureMessage = null;
            LastMigrationChain = resolved.Chain;
            logger.Info(
                "state",
                "state.restore_preflight_completed",
                "State restore preflight completed. Migration chain: " +
                FormatMigrationChain(resolved.Chain));
            return new StateRestorePlan(
                restorePlanOwner,
                snapshot,
                scope,
                operation,
                resolved.Value,
                resolved.Chain);
        }
        catch (Exception exception)
        {
            LastFailureMessage = exception.Message;
            LastMigrationChain = attemptedMigrationChain;
            SetStatus(StateManagerStatus.Idle);
            logger.Warning(
                "state",
                "state.restore_preflight_rejected",
                $"State restore preflight rejected: {exception.Message}");
            throw;
        }
        finally
        {
            transactions.Release();
        }
    }

    public async Task ExecuteAsync(
        StateRestorePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(plan.Owner, restorePlanOwner))
        {
            throw new StateRuntimeException("State restore plan belongs to another manager.");
        }

        EnsureInitialized();
        await transactions.WaitAsync(cancellationToken);
        IInputHandle? inputMuter = null;
        var mutationStarted = false;
        try
        {
            EnsureReadyForTransaction("restore");
            EnsureEligible(plan.Operation);
            SetStatus(StateManagerStatus.Restoring);
            logger.Info(
                "state",
                "state.restore_destructive_started",
                $"Destructive state restore started for scope '{plan.Scope}'.");
            inputMuter = input.AcquireMuter(this, new InputMuteOptions(name: "state-restore"));
            mutationStarted = true;
            await dialogue.StopAsync(cancellationToken);
            await CancelActiveOperationsAsync(cancellationToken);

            var durable = PersistentContributors(plan.Scope);
            foreach (var contributor in durable.Reverse())
            {
                await contributor.ResetAsync(cancellationToken);
            }

            foreach (var contributor in durable)
            {
                if (plan.Records.TryGetValue(contributor.Key, out var record))
                {
                    await contributor.RestoreAsync(record, cancellationToken);
                }
            }

            CurrentSnapshot = plan.Snapshot;
            LastFailureMessage = null;
            LastMigrationChain = plan.MigrationChain;
            SetStatus(StateManagerStatus.Idle);
            logger.Info(
                "state",
                "state.restore_completed",
                "State restore completed. Migration chain: " +
                FormatMigrationChain(plan.MigrationChain));
            Restored?.Invoke(plan.Snapshot);
        }
        catch (StateEligibilityException exception)
        {
            LastFailureMessage = exception.Message;
            SetStatus(StateManagerStatus.Idle);
            logger.Warning("state", "state.restore_rejected", exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            if (mutationStarted && inputMuter is not null)
            {
                faultInputMuter = inputMuter;
                inputMuter = null;
            }

            SetStatus(StateManagerStatus.Faulted);
            LastFailureMessage = exception.Message;
            logger.Error(
                "state",
                "state.restore_failed",
                $"Destructive state restore failed: {exception.Message}",
                exception);
            Failed?.Invoke(exception);
            throw;
        }
        finally
        {
            inputMuter?.Dispose();
            transactions.Release();
        }
    }

    private IStateContributor[] PersistentContributors(StateScope scope) =>
        contributors.Where(value =>
            value.Scope == scope && value.Capability.SupportsPersistence).ToArray();

    private static void ValidateSnapshotFormat(StateSnapshot snapshot)
    {
        if (snapshot.FormatVersion != StateSnapshot.CurrentFormatVersion)
        {
            throw new StateCompatibilityException(
                $"Unsupported state snapshot format '{snapshot.FormatVersion}'; " +
                $"expected '{StateSnapshot.CurrentFormatVersion}'.");
        }
    }

    private static void ValidateRecord(IStateContributor contributor, StateRecord record)
    {
        if (!string.Equals(record.Key, contributor.Key, StringComparison.Ordinal) ||
            record.Scope != contributor.Scope ||
            record.SchemaVersion != contributor.SchemaVersion)
        {
            throw new StateCompatibilityException(
                $"State record '{record.Key}' does not match contributor " +
                $"'{contributor.Key}' scope/schema.");
        }
    }

    private async Task CancelActiveOperationsAsync(CancellationToken cancellationToken)
    {
        var active = operations.ActiveOperations.ToArray();
        foreach (var operation in active)
        {
            operation.Cancel();
        }

        var failures = new List<Exception>();
        foreach (var operation in active)
        {
            try
            {
                await operation.Completion.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (operation.State == OperationState.Cancelled)
            {
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more active operations failed during state restore.",
                failures);
        }
    }

    private void EnsureEligible(StateOperationKind operation)
    {
        var result = eligibility.Check(operation);
        if (!result.IsAllowed)
        {
            throw new StateEligibilityException(result);
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new StateRuntimeException("StateManager is not initialized.");
        }
    }

    private void EnsureReadyForTransaction(string operation)
    {
        if (Status != StateManagerStatus.Idle)
        {
            throw new StateRuntimeException(
                $"Cannot {operation} state while StateManager is '{Status}'.");
        }
    }

    private void ReleaseFaultMuter()
    {
        faultInputMuter?.Dispose();
        faultInputMuter = null;
    }

    private void SetStatus(StateManagerStatus status)
    {
        if (Status == status)
        {
            return;
        }

        var previous = Status;
        Status = status;
        StatusChanged?.Invoke(new StateStatusChanged(previous, status));
    }

    private static string FormatMigrationChain(IReadOnlyList<string> chain) =>
        chain.Count == 0 ? "<none>" : string.Join(" -> ", chain);

    private sealed class PermissiveStateEligibility : IStateEligibility
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public StateEligibilityResult Check(StateOperationKind operation) => new(operation, []);
        public IStateBarrierHandle AcquireBarrier(
            object owner,
            string reasonCode,
            StateOperationMask operations = StateOperationMask.All,
            string? diagnostic = null,
            string? playerMessage = null) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        ReleaseFaultMuter();
        transactions.Dispose();
    }
}
