#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Save;

public interface ISaveThumbnailProvider : IRuntimeService
{
    Task<SaveThumbnailResult> CaptureAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string logicalReference,
        CancellationToken cancellationToken = default);

    Task IRuntimeService.InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
    Task IRuntimeService.ResetAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
    Task IRuntimeService.ShutdownAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public interface ISaveSlotManager : IRuntimeService
{
    event Action<SaveSlotOperationInfo>? OperationStarted;
    event Action<SaveSlotOperationInfo>? OperationProgressed;
    event Action<SaveSlotOperationInfo>? OperationCompleted;
    event Action<SaveSlotOperationInfo>? OperationFailed;
    event Action? SlotsChanged;

    SaveSlotManagerStatus Status { get; }
    SaveSlotOperationInfo? CurrentOperation { get; }
    SaveSlotId? CurrentSlot { get; }
    IReadOnlyDictionary<SaveSlotKind, int> Capacities { get; }

    SaveSlotAvailability CanSave(SaveSlotId slot);

    Task<SaveSlotAvailability> CanLoadAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaveSlotMetadata>> ListAsync(
        SaveSlotKind? kind = null,
        CancellationToken cancellationToken = default);

    Task<SaveSlotMetadata> ReadMetadataAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default);

    Task<SaveSlotMetadata> SaveAsync(
        SaveSlotId slot,
        SaveSlotSaveRequest? request = null,
        CancellationToken cancellationToken = default);

    Task<SaveSlotMetadata> SaveRotatingAsync(
        SaveSlotKind kind,
        SaveSlotSaveRequest? request = null,
        CancellationToken cancellationToken = default);

    Task LoadAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default);

    Task<SaveSlotMetadata> RenameAsync(
        SaveSlotId slot,
        string? playerName,
        CancellationToken cancellationToken = default);
}

public sealed class NullSaveThumbnailProvider : ISaveThumbnailProvider
{
    public static NullSaveThumbnailProvider Instance { get; } = new();

    private NullSaveThumbnailProvider() { }

    public Task<SaveThumbnailResult> CaptureAsync(
        SaveSlotId slot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SaveThumbnailResult(null, null));
    }

    public Task DeleteAsync(
        string logicalReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
