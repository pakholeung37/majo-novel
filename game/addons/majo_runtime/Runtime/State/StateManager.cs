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

public sealed class StateManager : IStateManager
{
    private readonly SemaphoreSlim transactions = new(1, 1);
    private readonly IReadOnlyList<IStateContributor> contributors;
    private readonly IDialoguePlayer dialogue;
    private readonly IInputManager input;
    private readonly IOperationScheduler operations;
    private readonly IRuntimeLogger logger;
    private IInputHandle? faultInputMuter;
    private bool isInitialized;

    public IReadOnlyList<string> RegisteredContributorKeys =>
        contributors.Select(contributor => contributor.Key).ToArray();

    public string? LastFailureMessage { get; private set; }

    public StateManager(
        IReadOnlyList<IStateContributor> contributors,
        IDialoguePlayer dialogue,
        IInputManager input,
        IOperationScheduler operations,
        IRuntimeLogger? logger = null)
    {
        this.contributors = contributors ??
            throw new ArgumentNullException(nameof(contributors));
        this.dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public event Action<StateStatusChanged>? StatusChanged;

    public event Action<StateSnapshot>? Captured;

    public event Action<StateSnapshot>? Restored;

    public event Action<Exception>? Failed;

    public StateManagerStatus Status { get; private set; } = StateManagerStatus.Idle;

    public StateSnapshot? CurrentSnapshot { get; private set; }

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
            foreach (var contributor in contributors.Where(
                         contributor => contributor.Scope == StateScope.Session).Reverse())
            {
                await contributor.ResetAsync(cancellationToken);
            }

            CurrentSnapshot = null;
            faultInputMuter?.Dispose();
            faultInputMuter = null;
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
            faultInputMuter?.Dispose();
            faultInputMuter = null;
            isInitialized = false;
            SetStatus(StateManagerStatus.Idle);
        }
        finally
        {
            transactions.Release();
        }
    }

    public async Task<StateSnapshot> CaptureGameAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await transactions.WaitAsync(cancellationToken);
        try
        {
            EnsureReadyForTransaction("capture");
            SetStatus(StateManagerStatus.Capturing);
            logger.Info("state", "state.capture_started", "State capture started.");
            await CompleteActiveOperationsAsync(cancellationToken);
            var records = new List<StateRecord>();
            foreach (var contributor in contributors.Where(
                         contributor => contributor.Scope == StateScope.Game))
            {
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
            SetStatus(StateManagerStatus.Idle);
            logger.Info("state", "state.capture_completed", $"State capture completed with {records.Count} records.");
            Captured?.Invoke(snapshot);
            return snapshot;
        }
        catch (Exception exception)
        {
            SetStatus(StateManagerStatus.Faulted);
            LastFailureMessage = exception.Message;
            logger.Error("state", "state.capture_failed", $"State capture failed: {exception.Message}", exception);
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
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureInitialized();
        await transactions.WaitAsync(cancellationToken);
        IInputHandle? inputMuter = null;
        var mutationStarted = false;
        try
        {
            EnsureReadyForTransaction("restore");
            ValidateSnapshotShape(snapshot);
            logger.Info("state", "state.restore_started", $"State restore started with format v{snapshot.FormatVersion}.");
            var gameContributors = contributors.Where(
                contributor => contributor.Scope == StateScope.Game).ToArray();
            foreach (var contributor in gameContributors)
            {
                var record = snapshot.Records[contributor.Key];
                ValidateRecord(contributor, record);
                await contributor.PreflightAsync(record, cancellationToken);
            }

            SetStatus(StateManagerStatus.Restoring);
            inputMuter = input.AcquireMuter(
                this,
                new InputMuteOptions(name: "state-restore"));
            mutationStarted = true;
            await dialogue.StopAsync(cancellationToken);
            await CancelActiveOperationsAsync(cancellationToken);
            foreach (var contributor in gameContributors.Reverse())
            {
                await contributor.ResetAsync(cancellationToken);
            }

            foreach (var contributor in gameContributors)
            {
                await contributor.RestoreAsync(
                    snapshot.Records[contributor.Key],
                    cancellationToken);
            }

            CurrentSnapshot = snapshot;
            LastFailureMessage = null;
            SetStatus(StateManagerStatus.Idle);
            logger.Info("state", "state.restore_completed", "State restore completed successfully.");
            Restored?.Invoke(snapshot);
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
            logger.Error("state", "state.restore_failed", $"State restore failed: {exception.Message}", exception);
            Failed?.Invoke(exception);
            throw;
        }
        finally
        {
            inputMuter?.Dispose();
            transactions.Release();
        }
    }

    private void ValidateSnapshotShape(StateSnapshot snapshot)
    {
        if (snapshot.FormatVersion != StateSnapshot.CurrentFormatVersion)
        {
            throw new StateSnapshotException(
                $"Unsupported state snapshot format '{snapshot.FormatVersion}'; " +
                $"expected '{StateSnapshot.CurrentFormatVersion}'.");
        }

        var expected = contributors
            .Where(contributor => contributor.Scope == StateScope.Game)
            .Select(contributor => contributor.Key)
            .ToHashSet(StringComparer.Ordinal);
        var actual = snapshot.Records.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(actual).OrderBy(value => value).ToArray();
        var unknown = actual.Except(expected).OrderBy(value => value).ToArray();
        if (missing.Length > 0 || unknown.Length > 0)
        {
            throw new StateSnapshotException(
                $"State snapshot records do not match the runtime. Missing: " +
                $"{FormatKeys(missing)}; unknown: {FormatKeys(unknown)}.");
        }
    }

    private static void ValidateRecord(
        IStateContributor contributor,
        StateRecord record)
    {
        if (!string.Equals(record.Key, contributor.Key, StringComparison.Ordinal) ||
            record.Scope != contributor.Scope ||
            record.SchemaVersion != contributor.SchemaVersion)
        {
            throw new StateSnapshotException(
                $"State record '{record.Key}' does not match contributor " +
                $"'{contributor.Key}' scope/schema.");
        }
    }

    private async Task CompleteActiveOperationsAsync(CancellationToken cancellationToken)
    {
        var active = operations.ActiveOperations.ToArray();
        foreach (var operation in active)
        {
            operation.Complete();
        }

        foreach (var operation in active)
        {
            await operation.Completion.WaitAsync(cancellationToken);
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
            catch (OperationCanceledException) when (
                operation.State == OperationState.Cancelled)
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

    private static string FormatKeys(IReadOnlyCollection<string> keys) =>
        keys.Count == 0 ? "<none>" : string.Join(", ", keys);
}
