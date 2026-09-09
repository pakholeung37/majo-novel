#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Input;
using Majo.Runtime.Script;

namespace Majo.Runtime.State;

public sealed class StateManager : IStateManager, IStateBoundarySource, IStateRestoreTransaction, IDisposable
{
    private readonly SemaphoreSlim transactions = new(1, 1);
    private readonly IReadOnlyList<IStateContributor> contributors;
    private readonly IInputManager input;
    private readonly IRuntimeRestoreBoundary restoreBoundary;
    private readonly IStateEligibility eligibility;
    private readonly StateMigrationRegistry migrations;
    private readonly IRuntimeLogger logger;
    private readonly object restorePlanOwner = new();
    private readonly StateMutationGate mutationGate = new();
    private IInputHandle? faultInputMuter;
    private IDisposable? faultPlaybackLease;
    private IRetainedRestoreOutput? faultOutput;
    private bool isInitialized;

    public StateManager(
        IReadOnlyList<IStateContributor> contributors,
        IInputManager input,
        IRuntimeRestoreBoundary restoreBoundary,
        IStateEligibility? eligibility = null,
        StateMigrationRegistry? migrations = null,
        IRuntimeLogger? logger = null,
        ILineBoundarySource? boundarySource = null)
    {
        this.boundarySource = boundarySource;
        this.contributors = contributors ?? throw new ArgumentNullException(nameof(contributors));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.restoreBoundary = restoreBoundary ?? throw new ArgumentNullException(nameof(restoreBoundary));
        this.eligibility = eligibility ?? new PermissiveStateEligibility();
        this.migrations = migrations ?? new StateMigrationRegistry();
        this.logger = logger ?? NullRuntimeLogger.Instance;
        if (this.eligibility is StateEligibility concreteEligibility)
        {
            concreteEligibility.Attach(() => Status, contributors,
                boundarySource is null ? null : () => CurrentBoundary is not null);
        }
    }

    private readonly ILineBoundarySource? boundarySource;
    private LineBoundaryContext? restoredBoundary;
    public event Action<CommittedLineBoundary>? BoundaryCommitted;
    public CommittedLineBoundary? CurrentBoundary { get; private set; }

    public event Action<StateStatusChanged>? StatusChanged;
    public event Action<StateSnapshot>? Captured;
    public event Action<StateSnapshot>? Restored;
    public event Action<Exception>? Failed;

    public StateManagerStatus Status { get; private set; } = StateManagerStatus.Idle;
    public StateSnapshot? CurrentSnapshot { get; private set; }
    public IStateRestoreTransaction RestoreTransaction => this;
    public TimeSpan RestorePresentationTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public IReadOnlyList<string> RegisteredContributorKeys =>
        contributors.Select(value => value.Key).ToArray();
    public string? LastFailureMessage { get; private set; }
    public IReadOnlyList<string> LastMigrationChain { get; private set; } = Array.Empty<string>();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var contributor in PersistentContributors(StateScope.Game))
        {
            if (contributor is not IPreparedStateContributor)
                throw new StateCompatibilityException($"Game contributor '{contributor.Key}' must implement IPreparedStateContributor.");
            if (contributor is IStateMutationParticipant participant)
                participant.BindStateMutationGate(mutationGate);
            else if (contributor is not ILineBoundaryOwnedStateContributor)
                throw new StateCompatibilityException(
                    $"Game contributor '{contributor.Key}' must implement IStateMutationParticipant so capture can isolate persistent writes.");
        }
        isInitialized = true;
        if (boundarySource is not null) boundarySource.CommitLineBoundaryAsync = CommitLineBoundaryAsync;
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
            CurrentBoundary = null;
            restoredBoundary = null;
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
            CurrentBoundary = null;
            restoredBoundary = null;
            LastMigrationChain = Array.Empty<string>();
            ReleaseFaultMuter();
            if (boundarySource is not null) boundarySource.CommitLineBoundaryAsync = null;
            CurrentBoundary = null;
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

    private async Task CommitLineBoundaryAsync(LineBoundaryContext boundary, CancellationToken token)
    {
        if (boundarySource?.IsCurrentBoundary(boundary) != true)
            throw new StateSnapshotException("The line boundary does not belong to the active playback preparation.");
        token.ThrowIfCancellationRequested();
        // Restores already own the State transaction and activate only after applying its records.
        if (boundary.Line.Origin is
            Majo.Runtime.Dialogue.DialoguePresentationOrigin.CheckpointRestore or
            Majo.Runtime.Dialogue.DialoguePresentationOrigin.HotReloadReplay)
        {
            restoredBoundary = boundary;
            return;
        }
        await CaptureCoreAsync(StateScope.Game, boundary, token);
    }

    public StateEnvelope CreateEnvelope(
        StateSnapshot snapshot,
        StateScope scope,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        IReadOnlyDictionary<string, string>? displayMetadata = null) =>
        StateEnvelopeHelper.CreateEnvelope(
            snapshot,
            scope,
            contributors,
            runtimeVersion,
            gameBuildId,
            storageNamespace,
            displayMetadata);

    public async Task<StateSnapshot> CaptureAsync(
        StateScope scope,
        CancellationToken cancellationToken = default)
    {
        if (scope == StateScope.Game)
            return (await CaptureSaveBoundaryAsync(cancellationToken)).Snapshot;
        return await CaptureCoreAsync(scope, null, cancellationToken);
    }

    public async Task<CommittedLineBoundary> CaptureSaveBoundaryAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (boundarySource is null)
            throw new StateSnapshotException("Saving a Game boundary requires a playback boundary source.");
        await boundarySource.WaitForPreparationAsync(cancellationToken);
        await transactions.WaitAsync(cancellationToken);
        try
        {
            EnsureReadyForTransaction("save boundary");
            EnsureEligible(StateOperationKind.Save);
            var current = CurrentBoundary ?? throw new StateEligibilityException(
                new StateEligibilityResult(StateOperationKind.Save,
                    [new StateEligibilityReason(StateEligibilityReasonCode.ScriptUnsafePhase,
                        "No committed line boundary is available to save.")]));
            // Between lines, writes belong to the next occurrence, not this saved script position.
            if (current.MutationRevision == mutationGate.Revision ||
                !boundarySource.CanReviseBoundary(current.Context)) return current;

            try
            {
                using var frozen = await mutationGate.FreezeAsync(cancellationToken);
                // Acquiring the freeze may have yielded while playback advanced.
                if (current.MutationRevision == mutationGate.Revision ||
                    !boundarySource.CanReviseBoundary(current.Context)) return current;
                SetStatus(StateManagerStatus.Capturing);
                var snapshot = await CaptureRecordsAsync(StateScope.Game, current.Context, cancellationToken);
                current = new CommittedLineBoundary(current.Context, snapshot, mutationGate.Revision);
                CurrentSnapshot = snapshot;
                CurrentBoundary = current;
                SetStatus(StateManagerStatus.Idle);
                logger.Info("state", "state.boundary_revision_committed",
                    "Committed a persistent-state revision for the active line occurrence.",
                    properties: new Dictionary<string, object?>
                    {
                        ["sessionGeneration"] = current.Context.SessionGeneration,
                        ["occurrenceSequence"] = current.Context.OccurrenceSequence,
                        ["lineId"] = current.Context.Script.Spot.LineId,
                        ["mutationRevision"] = current.MutationRevision,
                    });
                BoundaryCommitted?.Invoke(current);
                Captured?.Invoke(snapshot);
                return current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetStatus(StateManagerStatus.Idle);
                throw;
            }
            catch (Exception exception)
            {
                LastFailureMessage = exception.Message;
                SetStatus(StateManagerStatus.Faulted);
                logger.Error("state", "state.boundary_revision_failed",
                    $"Persistent-state boundary revision failed: {exception.Message}", exception);
                Failed?.Invoke(exception);
                throw;
            }
        }
        finally { transactions.Release(); }
    }

    private async Task<StateSnapshot> CaptureCoreAsync(
        StateScope scope,
        LineBoundaryContext? boundary,
        CancellationToken cancellationToken)
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
            if (scope == StateScope.Game && boundary is null)
                throw new StateSnapshotException(
                    "Game state can only be captured from a validated line boundary.");

            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            using var frozen = scope == StateScope.Game
                ? await mutationGate.FreezeAsync(cancellationToken)
                : null;
            SetStatus(StateManagerStatus.Capturing);
            logger.Info("state", "state.capture_started", $"State capture started for scope '{scope}'.");
            var snapshot = await CaptureRecordsAsync(scope, boundary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (boundary is not null && boundarySource?.IsCurrentBoundary(boundary) != true)
                throw new StateSnapshotException("The line boundary expired during capture.");
            CurrentSnapshot = snapshot;
            if (boundary is not null)
                CurrentBoundary = new CommittedLineBoundary(boundary, snapshot, mutationGate.Revision);
            LastFailureMessage = null;
            LastMigrationChain = Array.Empty<string>();
            SetStatus(StateManagerStatus.Idle);
            logger.Info(
                "state",
                "state.capture_completed",
                $"State capture completed with {snapshot.Records.Count} records for scope '{scope}'.");
            if (boundary is not null)
            {
                logger.Info("state", "state.boundary_committed", "Committed a line entry snapshot.",
                    properties: new Dictionary<string, object?>
                    {
                        ["sessionGeneration"] = boundary.SessionGeneration,
                        ["occurrenceSequence"] = boundary.OccurrenceSequence,
                        ["lineId"] = boundary.Script.Spot.LineId,
                        ["elapsedMs"] = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    });
                BoundaryCommitted?.Invoke(CurrentBoundary!);
            }
            Captured?.Invoke(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(StateManagerStatus.Idle);
            throw;
        }
        catch (StateEligibilityException exception)
        {
            LastFailureMessage = exception.Message;
            SetStatus(StateManagerStatus.Idle);
            // A missing committed boundary or rejected save is an expected caller result.
            logger.Debug("state", "state.capture_rejected", exception.Message);
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

    private async Task<StateSnapshot> CaptureRecordsAsync(
        StateScope scope,
        LineBoundaryContext? boundary,
        CancellationToken cancellationToken)
    {
        var records = new List<StateRecord>();
        foreach (var contributor in PersistentContributors(scope))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = boundary is not null && contributor is ScriptStateContributor script
                ? script.CaptureBoundary(boundary.Script)
                : await contributor.CaptureAsync(cancellationToken);
            ValidateRecord(contributor, record);
            records.Add(record);
        }
        return new StateSnapshot(StateSnapshot.CurrentFormatVersion, DateTimeOffset.UtcNow, records);
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
            await restoreBoundary.CancelForRestoreAsync(cancellationToken);
            foreach (var contributor in PersistentContributors(scope).Reverse())
            {
                await contributor.ResetAsync(cancellationToken);
            }
            CurrentSnapshot = null;
            CurrentBoundary = null;
            restoredBoundary = null;
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
        IReadOnlyList<string> attemptedMigrationChain = Array.Empty<string>();
        try
        {
            if (Status is StateManagerStatus.Restoring or StateManagerStatus.Faulted)
                throw new StateRuntimeException($"Cannot preflight restore while StateManager is '{Status}'.");
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
                if (contributor is not IPreparedStateContributor)
                    throw new StateCompatibilityException($"Contributor '{contributor.Key}' does not implement prepared replacement.");
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
            logger.Warning(
                "state",
                "state.restore_preflight_rejected",
                $"State restore preflight rejected: {exception.Message}");
            throw;
        }
    }

    public async Task ExecuteAsync(StateRestorePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(plan.Owner, restorePlanOwner))
            throw new StateRuntimeException("State restore plan belongs to another manager.");
        EnsureInitialized();
        EnsureEligible(plan.Operation);
        var oldBoundary = CurrentBoundary;
        // This cancels an in-flight boundary before waiting for the State gate it may own.
        IDisposable? playbackLease = boundarySource is null ? null :
            await boundarySource.AcquireRestoreAsync(cancellationToken);
        await transactions.WaitAsync(CancellationToken.None);
        var candidates = new List<IPreparedStateRestore>();
        IInputHandle? inputMuter = null;
        IRetainedRestoreOutput? retainedOutput = null;
        var commitStarted = false;
        var stage = "stop";
        try
        {
            EnsureReadyForTransaction("restore");
            EnsureEligible(plan.Operation);
            inputMuter = input.AcquireMuter(this, new InputMuteOptions(name: "state-restore"));
            SetStatus(StateManagerStatus.Restoring);
            restoredBoundary = null;
            retainedOutput = (restoreBoundary as IRuntimeRestoreOutput)?.RetainRestoreOutput();
            if (plan.Operation == StateOperationKind.HotReload &&
                restoreBoundary is IRuntimeHotReloadBoundary hotReloadBoundary)
                await hotReloadBoundary.CancelForHotReloadAsync(cancellationToken);
            else
                await restoreBoundary.CancelForRestoreAsync(cancellationToken);
            stage = "prepare";
            var context = new RestoreContext(plan.Scope, plan.Records, plan.Operation);
            foreach (var contributor in PersistentContributors(plan.Scope))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preparer = contributor as IPreparedStateContributor ?? throw new StateCompatibilityException(
                    $"Contributor '{contributor.Key}' does not implement prepared replacement.");
                plan.Records.TryGetValue(contributor.Key, out var record);
                var candidate = await preparer.PrepareRestoreAsync(record, context, cancellationToken);
                candidates.Add(candidate ?? throw new StateRuntimeException(
                    $"Contributor '{contributor.Key}' returned no restore candidate."));
            }
            logger.Info("state", "state.restore_prepared", "All restore candidates are prepared.");
            cancellationToken.ThrowIfCancellationRequested();
            stage = "commit";
            commitStarted = true;
            // No awaits, input, resource loading or cancellation points between commits.
            foreach (var candidate in candidates) candidate.Commit();
            logger.Info("state", "state.restore_committed", "Committed all prepared state replacements.");
            stage = "presentation";
            using (var deadline = new CancellationTokenSource(RestorePresentationTimeout))
            {
                foreach (var candidate in candidates.OfType<IStateRestorePresentation>())
                    await candidate.WaitForPresentationAsync(deadline.Token).WaitAsync(deadline.Token);
                if (retainedOutput is not null)
                    await retainedOutput.WaitForTargetAsync(deadline.Token).WaitAsync(deadline.Token);
            }
            logger.Info("state", "state.restore_presented", "Target presentation is ready for script activation.");
            stage = "activation";
            using (var deadline = new CancellationTokenSource(RestorePresentationTimeout))
                foreach (var activation in candidates.OfType<IStateRestoreActivation>())
                    await activation.ActivateAsync(deadline.Token).WaitAsync(deadline.Token);
            stage = "release";
            await DisposeCandidatesAsync(candidates);
            retainedOutput?.Dispose();
            retainedOutput = null;
            var committedSnapshot = restoredBoundary is null
                ? plan.Snapshot
                : ReconcileActivatedScriptSnapshot(plan.Snapshot, restoredBoundary.Script);
            CurrentSnapshot = committedSnapshot;
            CurrentBoundary = restoredBoundary is null
                ? null
                : new CommittedLineBoundary(restoredBoundary, committedSnapshot, mutationGate.Revision);
            restoredBoundary = null;
            LastFailureMessage = null;
            LastMigrationChain = plan.MigrationChain;
            SetStatus(StateManagerStatus.Idle);
            Restored?.Invoke(committedSnapshot);
            if (plan.Operation == StateOperationKind.HotReload && CurrentBoundary is not null)
                BoundaryCommitted?.Invoke(CurrentBoundary);
        }
        catch (Exception failure)
        {
            Exception reported = failure;
            var faulted = commitStarted;
            try { await DisposeCandidatesAsync(candidates); }
            catch (Exception cleanup)
            {
                reported = new AggregateException("Restore and candidate cleanup failed.", reported, cleanup);
                faulted = true;
            }
            if (!faulted && oldBoundary is not null && boundarySource is not null)
            {
                try
                {
                    var recoveryOrigin = plan.Operation == StateOperationKind.HotReload
                        ? Majo.Runtime.Dialogue.DialoguePresentationOrigin.HotReloadReplay
                        : Majo.Runtime.Dialogue.DialoguePresentationOrigin.CheckpointRestore;
                    await boundarySource.ResumeBoundaryAsync(
                        oldBoundary.Context.Script, recoveryOrigin, CancellationToken.None);
                    CurrentBoundary = oldBoundary;
                }
                catch (Exception recovery)
                {
                    reported = new AggregateException("Restore preparation and old boundary recovery failed.", reported, recovery);
                    faulted = true;
                }
            }
            restoredBoundary = null;
            if (faulted)
            {
                faultOutput = retainedOutput;
                retainedOutput = null;
                faultPlaybackLease = playbackLease;
                playbackLease = null;
                if (inputMuter is not null)
                {
                    faultInputMuter = inputMuter;
                    inputMuter = null;
                }
            }
            SetStatus(faulted ? StateManagerStatus.Faulted : StateManagerStatus.Idle);
            LastFailureMessage = reported.Message;
            logger.Error("state", "state.restore_failed", $"Restore failed during {stage}: {reported.Message}", reported,
                properties: new Dictionary<string, object?> { ["stage"] = stage, ["faulted"] = faulted });
            Failed?.Invoke(reported);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(reported).Throw();
            throw;
        }
        finally
        {
            retainedOutput?.Dispose();
            inputMuter?.Dispose();
            playbackLease?.Dispose();
            transactions.Release();
        }
    }

    private StateSnapshot ReconcileActivatedScriptSnapshot(
        StateSnapshot snapshot,
        ScriptCheckpoint checkpoint)
    {
        var script = contributors.OfType<ScriptStateContributor>().SingleOrDefault();
        if (script is null) return snapshot;
        var records = snapshot.Records.Values
            .Where(record => record.Key != BuiltInStateKeys.ScriptPlayback)
            .Append(script.CaptureBoundary(checkpoint));
        return new StateSnapshot(snapshot.FormatVersion, snapshot.CapturedAt, records);
    }

    private static async ValueTask DisposeCandidatesAsync(List<IPreparedStateRestore> candidates)
    {
        List<Exception>? failures = null;
        while (candidates.Count > 0)
        {
            var candidate = candidates[^1];
            candidates.RemoveAt(candidates.Count - 1);
            try { await candidate.DisposeAsync(); }
            catch (Exception failure) { (failures ??= new()).Add(failure); }
        }
        if (failures is not null) throw new AggregateException("Restore candidate cleanup failed.", failures);
    }

    private IStateContributor[] PersistentContributors(StateScope scope) =>
        contributors.Where(value =>
            value.Scope == scope && value.Capability.SupportsPersistence).ToArray();

    private static void ValidateSnapshotFormat(StateSnapshot snapshot) =>
        StateEnvelopeHelper.ValidateSnapshotFormat(snapshot);

    private static void ValidateRecord(IStateContributor contributor, StateRecord record) =>
        StateEnvelopeHelper.ValidateRecord(contributor, record);

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
        faultOutput?.Dispose();
        faultOutput = null;
        faultInputMuter?.Dispose();
        faultInputMuter = null;
        faultPlaybackLease?.Dispose();
        faultPlaybackLease = null;
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
        StateEnvelopeHelper.FormatMigrationChain(chain);

    public void Dispose()
    {
        ReleaseFaultMuter();
        transactions.Dispose();
    }
}
