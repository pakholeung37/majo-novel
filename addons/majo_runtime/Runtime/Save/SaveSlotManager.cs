#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.History;
using Majo.Runtime.State;

namespace Majo.Runtime.Save;

public sealed class SaveSlotManager : ISaveSlotManager, IDisposable
{
    private readonly SemaphoreSlim transaction = new(1, 1);
    private readonly IStateManager state;
    private readonly IStateSerializer serializer;
    private readonly IStateStorage storage;
    private readonly IStateEligibility eligibility;
    private readonly IGameSessionCoordinator sessions;
    private readonly IHistoryManager history;
    private readonly ISaveThumbnailProvider thumbnails;
    private readonly string runtimeVersion;
    private readonly string gameBuildId;
    private readonly string storageNamespace;
    private readonly IRuntimeLogger logger;
    private readonly ReadOnlyDictionary<SaveSlotKind, int> capacities;
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
        this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        ArgumentNullException.ThrowIfNull(capacities);
        var normalized = Enum.GetValues<SaveSlotKind>().ToDictionary(
            kind => kind,
            kind => capacities.TryGetValue(kind, out var capacity) && capacity > 0
                ? capacity
                : throw new ArgumentException(
                    $"A positive capacity is required for slot kind '{kind}'.",
                    nameof(capacities)));
        this.capacities = new ReadOnlyDictionary<SaveSlotKind, int>(normalized);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageNamespace);
        this.runtimeVersion = runtimeVersion.Trim();
        this.gameBuildId = gameBuildId.Trim();
        this.storageNamespace = storageNamespace.Trim();
        this.thumbnails = thumbnails ?? NullSaveThumbnailProvider.Instance;
        this.logger = logger ?? NullRuntimeLogger.Instance;
    }

    public event Action<SaveSlotOperationInfo>? OperationStarted;
    public event Action<SaveSlotOperationInfo>? OperationProgressed;
    public event Action<SaveSlotOperationInfo>? OperationCompleted;
    public event Action<SaveSlotOperationInfo>? OperationFailed;
    public event Action? SlotsChanged;

    public SaveSlotManagerStatus Status { get; private set; } = SaveSlotManagerStatus.Idle;
    public SaveSlotOperationInfo? CurrentOperation { get; private set; }
    public SaveSlotId? CurrentSlot { get; private set; }
    public IReadOnlyDictionary<SaveSlotKind, int> Capacities => capacities;
    public string? LastFailureMessage { get; private set; }
    public SaveSlotOperationPhase? LastFailurePhase { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        Status = SaveSlotManagerStatus.Idle;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentSlot = null;
        CurrentOperation = null;
        LastFailureMessage = null;
        LastFailurePhase = null;
        Status = SaveSlotManagerStatus.Idle;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentSlot = null;
        CurrentOperation = null;
        Status = SaveSlotManagerStatus.Idle;
        isInitialized = false;
        return Task.CompletedTask;
    }

    public SaveSlotAvailability CanSave(SaveSlotId slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var reasons = ValidateSlot(slot);
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
        var reasons = ValidateSlot(slot);
        if (IsBusy)
        {
            reasons.Add(new SaveSlotUnavailableReason(
                SaveSlotUnavailableReasonCode.Busy,
                $"Save slot manager is '{Status}'."));
        }
        AddEligibilityReasons(reasons, eligibility.Check(StateOperationKind.Restore));
        if (reasons.All(value => value.Code != SaveSlotUnavailableReasonCode.InvalidSlot))
        {
            var metadata = await ReadMetadataOrNullAsync(slot, cancellationToken);
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

    public async Task<IReadOnlyList<SaveSlotMetadata>> ListAsync(
        SaveSlotKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (kind is not null && !Enum.IsDefined(kind.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var keys = await storage.ListAsync(cancellationToken);
        var results = new List<SaveSlotMetadata>();
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseKey(key, out var slot) ||
                (kind is not null && slot.Kind != kind.Value) ||
                ValidateSlot(slot).Count > 0)
            {
                continue;
            }
            results.Add(await ReadMetadataCoreAsync(slot, cancellationToken));
        }

        return results
            .OrderBy(value => value.Id.Kind)
            .ThenByDescending(value => value.SavedAt)
            .ThenBy(value => value.Id.Index)
            .ToArray();
    }

    public Task<SaveSlotMetadata> ReadMetadataAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ThrowIfInvalid(slot);
        return ReadMetadataCoreAsync(slot, cancellationToken);
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
        var operation = StartOperation(new SaveSlotId(kind, 1), SaveSlotManagerStatus.Saving);
        try
        {
            SetPhase(operation, SaveSlotOperationPhase.Eligibility);
            EnsureEligible(StateOperationKind.Save);
            var existing = await ListAsync(kind, cancellationToken);
            var used = existing.Select(value => value.Id.Index).ToHashSet();
            var free = Enumerable.Range(1, capacities[kind])
                .FirstOrDefault(index => !used.Contains(index));
            var target = free > 0
                ? new SaveSlotId(kind, free)
                : existing
                    .OrderBy(value => value.SavedAt ?? DateTimeOffset.MinValue)
                    .ThenBy(value => value.Id.Index)
                    .First().Id;
            operation = RetargetOperation(operation, target);
            var result = await SaveCoreAsync(target, request, operation, cancellationToken);
            CompleteOperation(operation);
            return result;
        }
        catch (OperationCanceledException)
        {
            CancelOperation();
            throw;
        }
        catch (Exception exception)
        {
            FailOperation(operation, exception);
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
                SetPhase(operation, SaveSlotOperationPhase.Eligibility);
                EnsureEligible(StateOperationKind.Restore);
                SetPhase(operation, SaveSlotOperationPhase.Read);
                var bytes = await storage.ReadAsync(ToKey(slot), token);
                SetPhase(operation, SaveSlotOperationPhase.Deserialize);
                var envelope = DeserializeCompatible(slot, bytes);
                SetPhase(operation, SaveSlotOperationPhase.Preflight);
                var plan = await state.RestoreTransaction.PreflightAsync(
                    envelope.Snapshot,
                    StateScope.Game,
                    StateOperationKind.Restore,
                    token);
                SetPhase(operation, SaveSlotOperationPhase.Restore);
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
                SetPhase(operation, SaveSlotOperationPhase.Read);
                var old = await ReadMetadataCoreAsync(slot, token);
                SetPhase(operation, SaveSlotOperationPhase.Delete);
                await storage.DeleteAsync(ToKey(slot), token);
                await DeleteThumbnailBestEffortAsync(
                    old.ThumbnailReference,
                    CancellationToken.None);
                if (CurrentSlot == slot)
                {
                    CurrentSlot = null;
                }
                PublishSlotsChanged();
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
                SetPhase(operation, SaveSlotOperationPhase.Read);
                var bytes = await storage.ReadAsync(ToKey(slot), token);
                SetPhase(operation, SaveSlotOperationPhase.Deserialize);
                var envelope = DeserializeCompatible(slot, bytes);
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
                SetPhase(operation, SaveSlotOperationPhase.Rename);
                await storage.WriteAsync(ToKey(slot), serializer.Serialize(renamed), token);
                var metadata = ToMetadata(slot, renamed);
                PublishSlotsChanged();
                return metadata;
            },
            cancellationToken);

    private async Task<SaveSlotMetadata> SaveCoreAsync(
        SaveSlotId slot,
        SaveSlotSaveRequest? request,
        SaveSlotOperationInfo operation,
        CancellationToken cancellationToken)
    {
        ThrowIfInvalid(slot);
        SetPhase(operation, SaveSlotOperationPhase.Eligibility);
        EnsureEligible(StateOperationKind.Save);
        var oldMetadata = await ReadMetadataOrNullAsync(slot, cancellationToken);
        SetPhase(operation, SaveSlotOperationPhase.Capture);
        var snapshot = await state.CaptureGameAsync(cancellationToken);
        var display = BuildDisplayMetadata(slot, request, snapshot);
        string? capturedThumbnail = null;
        if (request?.CaptureThumbnail != false)
        {
            SetPhase(operation, SaveSlotOperationPhase.Thumbnail);
            SaveThumbnailResult thumbnail;
            try
            {
                thumbnail = await thumbnails.CaptureAsync(slot, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                thumbnail = SaveThumbnailResult.Missing(exception.Message);
                logger.Warning(
                    "save",
                    "save.thumbnail_failed",
                    $"Thumbnail capture failed for slot '{slot}': {exception.Message}",
                    exception);
            }
            if (thumbnail.LogicalReference is not null)
            {
                capturedThumbnail = thumbnail.LogicalReference;
                display[SaveSlotMetadataKeys.ThumbnailReference] = thumbnail.LogicalReference;
            }
            if (thumbnail.FailureReason is not null)
            {
                display[SaveSlotMetadataKeys.ThumbnailFailure] =
                    NormalizeRequired(thumbnail.FailureReason, 240);
            }
        }

        StateEnvelope envelope;
        try
        {
            envelope = state.CreateEnvelope(
                snapshot,
                StateScope.Game,
                runtimeVersion,
                gameBuildId,
                storageNamespace,
                display);
            SetPhase(operation, SaveSlotOperationPhase.Serialize);
            var bytes = serializer.Serialize(envelope);
            SetPhase(operation, SaveSlotOperationPhase.Commit);
            await storage.WriteAsync(ToKey(slot), bytes, cancellationToken);
        }
        catch
        {
            await DeleteThumbnailBestEffortAsync(capturedThumbnail, CancellationToken.None);
            throw;
        }
        var metadata = ToMetadata(slot, envelope);
        if (oldMetadata?.ThumbnailReference != metadata.ThumbnailReference)
        {
            await DeleteThumbnailBestEffortAsync(
                oldMetadata?.ThumbnailReference,
                CancellationToken.None);
        }
        PublishSlotsChanged();
        return metadata;
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
        ThrowIfInvalid(slot);
        await transaction.WaitAsync(cancellationToken);
        var operation = StartOperation(slot, status);
        try
        {
            var result = await action(operation, cancellationToken);
            CompleteOperation(operation);
            return result;
        }
        catch (OperationCanceledException)
        {
            CancelOperation();
            throw;
        }
        catch (Exception exception)
        {
            FailOperation(operation, exception);
            throw;
        }
        finally
        {
            transaction.Release();
        }
    }

    private SaveSlotOperationInfo StartOperation(
        SaveSlotId slot,
        SaveSlotManagerStatus status)
    {
        Status = status;
        LastFailureMessage = null;
        LastFailurePhase = null;
        CurrentOperation = new SaveSlotOperationInfo(
            Guid.NewGuid().ToString("N"),
            slot,
            status,
            SaveSlotOperationPhase.Eligibility,
            DateTimeOffset.UtcNow);
        logger.Info(
            "save",
            "save.operation_started",
            $"Save slot transaction '{CurrentOperation.TransactionId}' started for '{slot}' ({status}).");
        Publish(OperationStarted, CurrentOperation, "save.started_observer_failed");
        return CurrentOperation;
    }

    private SaveSlotOperationInfo RetargetOperation(
        SaveSlotOperationInfo operation,
        SaveSlotId target)
    {
        CurrentOperation = operation with { Slot = target };
        Publish(OperationProgressed, CurrentOperation, "save.progress_observer_failed");
        return CurrentOperation;
    }

    private void SetPhase(SaveSlotOperationInfo operation, SaveSlotOperationPhase phase)
    {
        CurrentOperation = operation with { Phase = phase };
        Publish(OperationProgressed, CurrentOperation, "save.progress_observer_failed");
        logger.Debug(
            "save",
            "save.operation_phase",
            $"Save slot transaction '{operation.TransactionId}' entered phase '{phase}'.");
    }

    private void CompleteOperation(SaveSlotOperationInfo operation)
    {
        CurrentOperation = operation with { Phase = SaveSlotOperationPhase.Completed };
        Status = SaveSlotManagerStatus.Idle;
        Publish(OperationCompleted, CurrentOperation, "save.completed_observer_failed");
        logger.Info(
            "save",
            "save.operation_completed",
            $"Save slot transaction '{operation.TransactionId}' completed for '{operation.Slot}'.");
    }

    private void CancelOperation()
    {
        CurrentOperation = null;
        Status = SaveSlotManagerStatus.Idle;
    }

    private void FailOperation(SaveSlotOperationInfo operation, Exception exception)
    {
        var phase = CurrentOperation?.Phase ?? operation.Phase;
        CurrentOperation = operation with
        {
            Phase = SaveSlotOperationPhase.Faulted,
            FailureMessage = exception.Message,
        };
        Status = SaveSlotManagerStatus.Faulted;
        LastFailureMessage = exception.Message;
        LastFailurePhase = phase;
        Publish(OperationFailed, CurrentOperation, "save.failed_observer_failed");
        logger.Error(
            "save",
            "save.operation_failed",
            $"Save slot transaction '{operation.TransactionId}' failed during '{phase}': {exception.Message}",
            exception);
    }

    private async Task<SaveSlotMetadata> ReadMetadataCoreAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken)
    {
        var bytes = await storage.ReadAsync(ToKey(slot), cancellationToken);
        try
        {
            var envelope = DeserializeCompatible(slot, bytes);
            return ToMetadata(slot, envelope);
        }
        catch (StateCompatibilityException exception)
        {
            return UnavailableMetadata(slot, SaveSlotCompatibility.Incompatible, exception.Message);
        }
        catch (StateRuntimeException exception)
        {
            return UnavailableMetadata(slot, SaveSlotCompatibility.Corrupt, exception.Message);
        }
    }

    private async Task<SaveSlotMetadata?> ReadMetadataOrNullAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadMetadataCoreAsync(slot, cancellationToken);
        }
        catch (StateStorageNotFoundException)
        {
            return null;
        }
    }

    private StateEnvelope DeserializeCompatible(SaveSlotId slot, ReadOnlySpan<byte> bytes)
    {
        var envelope = serializer.Deserialize(bytes);
        if (envelope.Scope != StateScope.Game)
        {
            throw new StateCompatibilityException(
                $"Save slot '{slot}' contains scope '{envelope.Scope}', expected Game.");
        }
        if (!string.Equals(envelope.StorageNamespace, storageNamespace, StringComparison.Ordinal))
        {
            throw new StateCompatibilityException(
                $"Save slot '{slot}' belongs to namespace '{envelope.StorageNamespace}'.");
        }
        if (!MetadataMatchesSlot(envelope.DisplayMetadata, slot))
        {
            throw new StateCompatibilityException(
                $"Save slot '{slot}' metadata does not match its logical key.");
        }
        return envelope;
    }

    private SortedDictionary<string, string> BuildDisplayMetadata(
        SaveSlotId slot,
        SaveSlotSaveRequest? request,
        StateSnapshot snapshot)
    {
        var display = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [SaveSlotMetadataKeys.SlotKind] = slot.Kind.ToString(),
            [SaveSlotMetadataKeys.SlotIndex] = slot.Index.ToString(CultureInfo.InvariantCulture),
        };
        var playerName = NormalizeOptional(request?.PlayerName, 80);
        if (playerName is not null)
        {
            display[SaveSlotMetadataKeys.PlayerName] = playerName;
        }
        var boundary = (state as IStateBoundarySource)?.CurrentBoundary;
        if (boundary is not null && ReferenceEquals(boundary.Snapshot, snapshot))
        {
            var line = boundary.Context.Line;
            if (!string.IsNullOrWhiteSpace(line.Speaker))
                display[SaveSlotMetadataKeys.Speaker] = NormalizeRequired(line.Speaker, 80);
            display[SaveSlotMetadataKeys.TextSummary] = NormalizeRequired(line.Text.Text, 240);
            if (!string.IsNullOrWhiteSpace(line.ResolvedLocale))
                display[SaveSlotMetadataKeys.Locale] = NormalizeRequired(line.ResolvedLocale, 40);
        }
        else if (history.Entries.Count > 0 && history.Entries[^1] is { } entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.Speaker))
                display[SaveSlotMetadataKeys.Speaker] = NormalizeRequired(entry.Speaker, 80);
            display[SaveSlotMetadataKeys.TextSummary] = NormalizeRequired(entry.Text, 240);
            if (!string.IsNullOrWhiteSpace(entry.Locale))
                display[SaveSlotMetadataKeys.Locale] = NormalizeRequired(entry.Locale, 40);
        }
        foreach (var pair in request?.DeveloperMetadata ??
                     new Dictionary<string, string>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            if (pair.Key.Length > 80 || pair.Key.StartsWith("majo.", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Developer metadata key '{pair.Key}' is invalid.",
                    nameof(request));
            }
            display[SaveSlotMetadataKeys.DeveloperPrefix + pair.Key] =
                NormalizeRequired(pair.Value, 240);
        }
        return display;
    }

    private static SaveSlotMetadata ToMetadata(SaveSlotId slot, StateEnvelope envelope)
    {
        var display = envelope.DisplayMetadata;
        var developer = display
            .Where(value => value.Key.StartsWith(
                SaveSlotMetadataKeys.DeveloperPrefix,
                StringComparison.Ordinal))
            .ToDictionary(
                value => value.Key[SaveSlotMetadataKeys.DeveloperPrefix.Length..],
                value => value.Value,
                StringComparer.Ordinal);
        return new SaveSlotMetadata(
            slot,
            SaveSlotCompatibility.Compatible,
            envelope.CreatedAt,
            envelope.GameBuildId,
            envelope.RuntimeVersion,
            envelope.Script?.ProjectId,
            envelope.Script?.NodeName,
            envelope.Script?.LineId,
            Get(display, SaveSlotMetadataKeys.Speaker),
            Get(display, SaveSlotMetadataKeys.TextSummary),
            Get(display, SaveSlotMetadataKeys.PlayerName),
            Get(display, SaveSlotMetadataKeys.ThumbnailReference),
            Get(display, SaveSlotMetadataKeys.ThumbnailFailure),
            new ReadOnlyDictionary<string, string>(developer),
            Locale: Get(display, SaveSlotMetadataKeys.Locale));
    }

    private static SaveSlotMetadata UnavailableMetadata(
        SaveSlotId slot,
        SaveSlotCompatibility compatibility,
        string message) => new(
            slot,
            compatibility,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()),
            message);

    private static StateEnvelope CloneEnvelope(
        StateEnvelope envelope,
        IReadOnlyDictionary<string, string> display) => new(
            envelope.FormatVersion,
            envelope.RuntimeVersion,
            envelope.GameBuildId,
            envelope.StorageNamespace,
            envelope.CreatedAt,
            envelope.Scope,
            envelope.Snapshot,
            envelope.Records,
            envelope.Script,
            display,
            envelope.MigrationChain);

    private async Task DeleteThumbnailBestEffortAsync(
        string? logicalReference,
        CancellationToken cancellationToken)
    {
        if (logicalReference is null)
        {
            return;
        }
        try
        {
            await thumbnails.DeleteAsync(logicalReference, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.Warning(
                "save",
                "save.thumbnail_cleanup_failed",
                $"Could not clean thumbnail '{logicalReference}': {exception.Message}",
                exception);
        }
    }

    private List<SaveSlotUnavailableReason> ValidateSlot(SaveSlotId slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var reasons = new List<SaveSlotUnavailableReason>();
        if (!capacities.TryGetValue(slot.Kind, out var capacity) || slot.Index > capacity)
        {
            reasons.Add(new SaveSlotUnavailableReason(
                SaveSlotUnavailableReasonCode.InvalidSlot,
                $"Slot '{slot}' exceeds configured capacity."));
        }
        return reasons;
    }

    private void ThrowIfInvalid(SaveSlotId slot)
    {
        var availability = new SaveSlotAvailability(ValidateSlot(slot));
        if (!availability.IsAllowed)
        {
            throw new SaveSlotUnavailableException(availability);
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

    private static string ToKey(SaveSlotId slot) =>
        $"slots/{slot.Kind.ToString().ToLowerInvariant()}/{slot.Index:D4}";

    private static bool TryParseKey(string key, out SaveSlotId slot)
    {
        slot = null!;
        var parts = key.Split('/');
        if (parts.Length != 3 || parts[0] != "slots" ||
            !Enum.TryParse<SaveSlotKind>(parts[1], true, out var kind) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            index <= 0)
        {
            return false;
        }
        slot = new SaveSlotId(kind, index);
        return true;
    }

    private static bool MetadataMatchesSlot(
        IReadOnlyDictionary<string, string> metadata,
        SaveSlotId slot) =>
        Get(metadata, SaveSlotMetadataKeys.SlotKind) == slot.Kind.ToString() &&
        Get(metadata, SaveSlotMetadataKeys.SlotIndex) ==
            slot.Index.ToString(CultureInfo.InvariantCulture);

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string? NormalizeOptional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : NormalizeRequired(value, maxLength);

    private static string NormalizeRequired(string value, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new SaveSlotException("Save slot manager is not initialized.");
        }
    }

    private void PublishSlotsChanged() => Publish(
        SlotsChanged,
        "save.slots_changed_observer_failed");

    private void Publish(Action? observers, string eventId)
    {
        if (observers is null)
        {
            return;
        }
        foreach (Action observer in observers.GetInvocationList())
        {
            try
            {
                observer();
            }
            catch (Exception exception)
            {
                logger.Warning(
                    "save",
                    eventId,
                    $"Save slot observer failed: {exception.Message}",
                    exception);
            }
        }
    }

    private void Publish<T>(Action<T>? observers, T value, string eventId)
    {
        if (observers is null)
        {
            return;
        }
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try
            {
                observer(value);
            }
            catch (Exception exception)
            {
                logger.Warning(
                    "save",
                    eventId,
                    $"Save slot observer failed: {exception.Message}",
                    exception);
            }
        }
    }

    public void Dispose() => transaction.Dispose();
}
