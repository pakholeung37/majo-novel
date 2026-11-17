#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.History;
using Majo.Runtime.State;
using static Majo.Runtime.Save.SaveSlotMetadataCodec;

namespace Majo.Runtime.Save;

public sealed class SaveSlotManager : ISaveSlotManager, IDisposable
{
    private readonly SemaphoreSlim transaction = new(1, 1);
    private readonly IStateManager state;
    private readonly IStateEligibility eligibility;
    private readonly IGameSessionCoordinator sessions;
    private readonly SaveSlotCatalog catalog;
    private readonly SaveSlotRepository repository;
    private readonly SaveSlotSaveTransaction saveTransaction;
    private readonly SaveSlotOperationTracker operations;
    private bool isInitialized;

    public SaveSlotManager(
        IStateManager state,
        IStateSerializer serializer,
        IStateStorage storage,
        IStateEligibility eligibility,
        IGameSessionCoordinator sessions,
        IHistoryManager history,
        IReadOnlyDictionary<SaveSlotKind, int> capacities,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        ISaveThumbnailProvider? thumbnails = null,
        IRuntimeLogger? logger = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(history);
        this.eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        catalog = new SaveSlotCatalog(capacities);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageNamespace);
        var runtimeLogger = logger ?? NullRuntimeLogger.Instance;
        repository = new SaveSlotRepository(serializer, storage, catalog, storageNamespace.Trim());
        saveTransaction = new SaveSlotSaveTransaction(state, repository, history, thumbnails ?? NullSaveThumbnailProvider.Instance,
            runtimeVersion.Trim(), gameBuildId.Trim(), storageNamespace.Trim(), runtimeLogger);
        operations = new SaveSlotOperationTracker(runtimeLogger);
    }

    public event Action<SaveSlotOperationInfo>? OperationStarted { add => operations.OperationStarted += value; remove => operations.OperationStarted -= value; }
    public event Action<SaveSlotOperationInfo>? OperationProgressed { add => operations.OperationProgressed += value; remove => operations.OperationProgressed -= value; }
    public event Action<SaveSlotOperationInfo>? OperationCompleted { add => operations.OperationCompleted += value; remove => operations.OperationCompleted -= value; }
    public event Action<SaveSlotOperationInfo>? OperationFailed { add => operations.OperationFailed += value; remove => operations.OperationFailed -= value; }
    public event Action? SlotsChanged { add => operations.SlotsChanged += value; remove => operations.SlotsChanged -= value; }
    public SaveSlotManagerStatus Status => operations.Status;
    public SaveSlotOperationInfo? CurrentOperation => operations.CurrentOperation;
    public SaveSlotId? CurrentSlot { get; private set; }
    public IReadOnlyDictionary<SaveSlotKind, int> Capacities => catalog.Capacities;
    public string? LastFailureMessage => operations.LastFailureMessage;
    public SaveSlotOperationPhase? LastFailurePhase => operations.LastFailurePhase;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        operations.Initialize();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentSlot = null;
        operations.Reset();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentSlot = null;
        operations.Reset(clearFailure: false);
        isInitialized = false;
        return Task.CompletedTask;
    }

    public SaveSlotAvailability CanSave(SaveSlotId slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var reasons = catalog.ValidateSlot(slot);
        if (IsBusy)
        {
            reasons.Add(new SaveSlotUnavailableReason(
                SaveSlotUnavailableReasonCode.Busy,
                $"Save slot manager is '{Status}'."));
        }
        AddEligibilityReasons(reasons, eligibility.Check(StateOperationKind.Save));
        return new SaveSlotAvailability(reasons.AsReadOnly());
    }

    public async Task<SaveSlotAvailability> CanLoadAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var reasons = catalog.ValidateSlot(slot);
        if (IsBusy)
        {
            reasons.Add(new SaveSlotUnavailableReason(
                SaveSlotUnavailableReasonCode.Busy,
                $"Save slot manager is '{Status}'."));
        }
        AddEligibilityReasons(reasons, eligibility.Check(StateOperationKind.Restore));
        if (reasons.All(value => value.Code != SaveSlotUnavailableReasonCode.InvalidSlot))
        {
            var metadata = await repository.ReadMetadataOrNullAsync(slot, cancellationToken);
            if (metadata is null)
            {
                reasons.Add(new SaveSlotUnavailableReason(
                    SaveSlotUnavailableReasonCode.NotFound,
                    $"Save slot '{slot}' does not exist."));
            }
            else if (metadata.Compatibility != SaveSlotCompatibility.Compatible)
            {
                reasons.Add(new SaveSlotUnavailableReason(
                    metadata.Compatibility == SaveSlotCompatibility.Corrupt
                        ? SaveSlotUnavailableReasonCode.Corrupt
                        : SaveSlotUnavailableReasonCode.Incompatible,
                    metadata.FailureMessage ?? $"Save slot '{slot}' is unavailable."));
            }
        }
        return new SaveSlotAvailability(reasons.AsReadOnly());
    }

    public Task<IReadOnlyList<SaveSlotMetadata>> ListAsync(SaveSlotKind? kind = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return repository.ListAsync(kind, cancellationToken);
    }

    public Task<SaveSlotMetadata> ReadMetadataAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        catalog.ThrowIfInvalid(slot);
        return repository.ReadMetadataCoreAsync(slot, cancellationToken);
    }

    public Task<SaveSlotMetadata> SaveAsync(
        SaveSlotId slot,
        SaveSlotSaveRequest? request = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            slot,
            SaveSlotManagerStatus.Saving,
            (operation, token) => SaveCoreAsync(slot, request, operation, token),
            cancellationToken);

    public async Task<SaveSlotMetadata> SaveRotatingAsync(
        SaveSlotKind kind,
        SaveSlotSaveRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (kind == SaveSlotKind.Manual || !Enum.IsDefined(kind))
        {
            throw new ArgumentException(
                "Only Quick and Auto slots use rotating save chains.",
                nameof(kind));
        }

        EnsureInitialized();
        await transaction.WaitAsync(cancellationToken);
        var operation = operations.StartOperation(new SaveSlotId(kind, 1), SaveSlotManagerStatus.Saving);
        try
        {
            operations.SetPhase(operation, SaveSlotOperationPhase.Eligibility);
            EnsureEligible(StateOperationKind.Save);
            var existing = await ListAsync(kind, cancellationToken);
            var target = SaveSlotRotationPolicy.DetermineNextRotatingSlot(kind, catalog.Capacities[kind], existing);
            operation = operations.RetargetOperation(operation, target);
            var result = await SaveCoreAsync(target, request, operation, cancellationToken);
            operations.CompleteOperation(operation);
            return result;
        }
        catch (OperationCanceledException)
        {
            operations.CancelOperation();
            throw;
        }
        catch (Exception exception)
        {
            operations.FailOperation(operation, exception);
            throw;
        }
        finally
        {
            transaction.Release();
        }
    }

    public Task LoadAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync<object?>(
            slot,
            SaveSlotManagerStatus.Loading,
            async (operation, token) =>
            {
                operations.SetPhase(operation, SaveSlotOperationPhase.Eligibility);
                EnsureEligible(StateOperationKind.Restore);
                operations.SetPhase(operation, SaveSlotOperationPhase.Read);
                var bytes = await repository.ReadAsync(slot, token);
                operations.SetPhase(operation, SaveSlotOperationPhase.Deserialize);
                var envelope = repository.DeserializeCompatible(slot, bytes);
                operations.SetPhase(operation, SaveSlotOperationPhase.Preflight);
                var plan = await state.RestoreTransaction.PreflightAsync(
                    envelope.Snapshot,
                    StateScope.Game,
                    StateOperationKind.Restore,
                    token);
                operations.SetPhase(operation, SaveSlotOperationPhase.Restore);
                await sessions.LoadGameAsync(plan, token);
                CurrentSlot = slot;
                return null;
            },
            cancellationToken);

    public Task DeleteAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync<object?>(
            slot,
            SaveSlotManagerStatus.Deleting,
            async (operation, token) =>
            {
                operations.SetPhase(operation, SaveSlotOperationPhase.Read);
                var old = await repository.ReadMetadataCoreAsync(slot, token);
                operations.SetPhase(operation, SaveSlotOperationPhase.Delete);
                await repository.DeleteAsync(slot, token);
                await saveTransaction.DeleteThumbnailBestEffortAsync(
                    old.ThumbnailReference,
                    CancellationToken.None);
                if (CurrentSlot == slot)
                {
                    CurrentSlot = null;
                }
                operations.PublishSlotsChanged();
                return null;
            },
            cancellationToken);

    public Task<SaveSlotMetadata> RenameAsync(
        SaveSlotId slot,
        string? playerName,
        CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            slot,
            SaveSlotManagerStatus.Renaming,
            async (operation, token) =>
            {
                var normalizedName = NormalizeOptional(playerName, 80);
                operations.SetPhase(operation, SaveSlotOperationPhase.Read);
                var bytes = await repository.ReadAsync(slot, token);
                operations.SetPhase(operation, SaveSlotOperationPhase.Deserialize);
                var envelope = repository.DeserializeCompatible(slot, bytes);
                var display = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in envelope.DisplayMetadata)
                {
                    display.Add(pair.Key, pair.Value);
                }
                if (normalizedName is null)
                {
                    display.Remove(SaveSlotMetadataKeys.PlayerName);
                }
                else
                {
                    display[SaveSlotMetadataKeys.PlayerName] = normalizedName;
                }
                var renamed = CloneEnvelope(envelope, display);
                operations.SetPhase(operation, SaveSlotOperationPhase.Rename);
                await repository.WriteAsync(slot, repository.Serialize(renamed), token);
                var metadata = ToMetadata(slot, renamed);
                operations.PublishSlotsChanged();
                return metadata;
            },
            cancellationToken);

    private async Task<SaveSlotMetadata> SaveCoreAsync(SaveSlotId slot, SaveSlotSaveRequest? request,
        SaveSlotOperationInfo operation, CancellationToken cancellationToken)
    {
        catalog.ThrowIfInvalid(slot);
        operations.SetPhase(operation, SaveSlotOperationPhase.Eligibility);
        EnsureEligible(StateOperationKind.Save);
        var result = await saveTransaction.ExecuteAsync(slot, request, phase => operations.SetPhase(operation, phase), cancellationToken);
        operations.PublishSlotsChanged();
        return result;
    }

    private async Task<T> RunExclusiveAsync<T>(
        SaveSlotId slot,
        SaveSlotManagerStatus status,
        Func<SaveSlotOperationInfo, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(action);
        EnsureInitialized();
        catalog.ThrowIfInvalid(slot);
        await transaction.WaitAsync(cancellationToken);
        var operation = operations.StartOperation(slot, status);
        try
        {
            var result = await action(operation, cancellationToken);
            operations.CompleteOperation(operation);
            return result;
        }
        catch (OperationCanceledException)
        {
            operations.CancelOperation();
            throw;
        }
        catch (Exception exception)
        {
            operations.FailOperation(operation, exception);
            throw;
        }
        finally
        {
            transaction.Release();
        }
    }

    private void EnsureEligible(StateOperationKind operation)
    {
        var result = eligibility.Check(operation);
        if (!result.IsAllowed)
        {
            var reasons = new List<SaveSlotUnavailableReason>();
            AddEligibilityReasons(reasons, result);
            throw new SaveSlotUnavailableException(new SaveSlotAvailability(reasons));
        }
    }

    private static void AddEligibilityReasons(
        List<SaveSlotUnavailableReason> target,
        StateEligibilityResult result)
    {
        foreach (var reason in result.Reasons)
        {
            target.Add(new SaveSlotUnavailableReason(
                SaveSlotUnavailableReasonCode.StateIneligible,
                reason.Diagnostic,
                reason.PlayerMessage,
                reason.Code));
        }
    }

    private bool IsBusy => Status is SaveSlotManagerStatus.Saving or
        SaveSlotManagerStatus.Loading or SaveSlotManagerStatus.Deleting or
        SaveSlotManagerStatus.Renaming;

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new SaveSlotException("Save slot manager is not initialized.");
        }
    }

    public void Dispose() => transaction.Dispose();
}
