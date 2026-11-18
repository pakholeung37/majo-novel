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

internal sealed class SaveSlotSaveTransaction(
    IStateManager state, SaveSlotRepository repository, IHistoryManager history,
    ISaveThumbnailProvider thumbnails, string runtimeVersion, string gameBuildId,
    string storageNamespace, IRuntimeLogger logger)
{
    public async Task<SaveSlotMetadata> ExecuteAsync(SaveSlotId slot, SaveSlotSaveRequest? request,
        Action<SaveSlotOperationPhase> progress, CancellationToken cancellationToken)
    {
        var oldMetadata = await repository.ReadMetadataOrNullAsync(slot, cancellationToken);
        progress(SaveSlotOperationPhase.Capture);
        if (state is not IStateBoundarySource source)
            throw new StateCompatibilityException(
                "SaveSlotManager requires an IStateBoundarySource; live Game capture is not supported.");
        var boundary = await source.CaptureSaveBoundaryAsync(cancellationToken);
        var snapshot = boundary.Snapshot;
        var display = BuildDisplayMetadata(slot, request, boundary, history.Entries);
        string? capturedThumbnail = null;
        if (request?.CaptureThumbnail != false)
        {
            progress(SaveSlotOperationPhase.Thumbnail);
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
            progress(SaveSlotOperationPhase.Serialize);
            var bytes = repository.Serialize(envelope);
            progress(SaveSlotOperationPhase.Commit);
            await repository.WriteAsync(slot, bytes, cancellationToken);
        }
        catch
        {
            if (capturedThumbnail != oldMetadata?.ThumbnailReference)
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
        return metadata;
    }

    public async Task DeleteThumbnailBestEffortAsync(
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

}
