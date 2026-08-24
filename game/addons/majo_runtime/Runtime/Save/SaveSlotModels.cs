#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.State;

namespace Majo.Runtime.Save;

public enum SaveSlotKind
{
    Manual,
    Quick,
    Auto,
}

public sealed record SaveSlotId
{
    public SaveSlotId(SaveSlotKind kind, int index)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (index <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Kind = kind;
        Index = index;
    }

    public SaveSlotKind Kind { get; }
    public int Index { get; }

    public override string ToString() => $"{Kind}/{Index}";
}

public enum SaveSlotCompatibility
{
    Compatible,
    Corrupt,
    Incompatible,
}

public sealed record SaveSlotMetadata(
    SaveSlotId Id,
    SaveSlotCompatibility Compatibility,
    DateTimeOffset? SavedAt,
    string? GameBuildId,
    string? RuntimeVersion,
    string? ProjectId,
    string? NodeName,
    string? LineId,
    string? Speaker,
    string? TextSummary,
    string? PlayerName,
    string? ThumbnailReference,
    string? ThumbnailFailureReason,
    IReadOnlyDictionary<string, string> DeveloperMetadata,
    string? FailureMessage = null);

public sealed record SaveSlotSaveRequest(
    string? PlayerName = null,
    IReadOnlyDictionary<string, string>? DeveloperMetadata = null,
    bool CaptureThumbnail = true);

public enum SaveSlotUnavailableReasonCode
{
    Busy,
    InvalidSlot,
    NotFound,
    Corrupt,
    Incompatible,
    StateIneligible,
}

public sealed record SaveSlotUnavailableReason(
    SaveSlotUnavailableReasonCode Code,
    string Diagnostic,
    string? PlayerMessage = null,
    StateEligibilityReasonCode? StateReasonCode = null);

public sealed record SaveSlotAvailability(
    IReadOnlyList<SaveSlotUnavailableReason> Reasons)
{
    public bool IsAllowed => Reasons.Count == 0;
}

public enum SaveSlotManagerStatus
{
    Idle,
    Saving,
    Loading,
    Deleting,
    Renaming,
    Faulted,
}

public enum SaveSlotOperationPhase
{
    Eligibility,
    Capture,
    Thumbnail,
    Serialize,
    Commit,
    Read,
    Deserialize,
    Preflight,
    Restore,
    Delete,
    Rename,
    Completed,
    Faulted,
}

public sealed record SaveSlotOperationInfo(
    string TransactionId,
    SaveSlotId Slot,
    SaveSlotManagerStatus Status,
    SaveSlotOperationPhase Phase,
    DateTimeOffset StartedAt,
    string? FailureMessage = null);

public sealed record SaveThumbnailResult(
    string? LogicalReference,
    string? FailureReason)
{
    public static SaveThumbnailResult Missing(string reason) => new(null, reason);
}

public static class SaveSlotMetadataKeys
{
    public const string SlotKind = "majo.slot.kind";
    public const string SlotIndex = "majo.slot.index";
    public const string PlayerName = "majo.slot.playerName";
    public const string Speaker = "majo.slot.speaker";
    public const string TextSummary = "majo.slot.textSummary";
    public const string ThumbnailReference = "majo.slot.thumbnail";
    public const string ThumbnailFailure = "majo.slot.thumbnailFailure";
    public const string DeveloperPrefix = "developer.";
}
