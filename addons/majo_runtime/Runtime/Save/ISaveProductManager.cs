#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Save;

public sealed record AutoSaveCandidate(
    long BoundaryId,
    string? ProjectId,
    string? NodeName,
    string? LineId,
    string Source);

public interface ISaveProductManager : IRuntimeService
{
    event Action<AutoSaveCandidate>? AutoSaveCandidateAccepted;
    event Action<SaveSlotMetadata>? AutoSaveCommitted;
    event Action<Exception>? AutoSaveFailed;

    AutoSaveCandidate? PendingAutoSave { get; }
    long? LastCommittedAutoSaveBoundary { get; }

    Task<SaveSlotMetadata> QuickSaveAsync(
        CancellationToken cancellationToken = default);

    void RequestAutoSaveCandidate(string source = "project-api");
}
