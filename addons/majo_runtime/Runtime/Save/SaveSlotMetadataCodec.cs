#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Majo.Runtime.History;
using Majo.Runtime.State;

namespace Majo.Runtime.Save;

internal static class SaveSlotMetadataCodec
{
    public static string ToKey(SaveSlotId slot) =>
        $"slots/{slot.Kind.ToString().ToLowerInvariant()}/{slot.Index:D4}";

    public static bool TryParseKey(string key, out SaveSlotId slot)
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

    public static SortedDictionary<string, string> BuildDisplayMetadata(
        SaveSlotId slot,
        SaveSlotSaveRequest? request,
        CommittedLineBoundary? boundary,
        IReadOnlyList<HistoryEntry> historyEntries)
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
        if (boundary is not null)
        {
            var line = boundary.Context.Line;
            if (!string.IsNullOrWhiteSpace(line.Speaker))
                display[SaveSlotMetadataKeys.Speaker] = NormalizeRequired(line.Speaker, 80);
            display[SaveSlotMetadataKeys.TextSummary] = NormalizeRequired(line.Text.Text, 240);
            if (!string.IsNullOrWhiteSpace(line.ResolvedLocale))
                display[SaveSlotMetadataKeys.Locale] = NormalizeRequired(line.ResolvedLocale, 40);
        }
        else if (historyEntries.Count > 0 && historyEntries[^1] is { } entry)
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

    public static SaveSlotMetadata ToMetadata(SaveSlotId slot, StateEnvelope envelope)
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

    public static SaveSlotMetadata UnavailableMetadata(
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

    public static StateEnvelope CloneEnvelope(
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

    public static bool MetadataMatchesSlot(
        IReadOnlyDictionary<string, string> metadata,
        SaveSlotId slot) =>
        Get(metadata, SaveSlotMetadataKeys.SlotKind) == slot.Kind.ToString() &&
        Get(metadata, SaveSlotMetadataKeys.SlotIndex) ==
            slot.Index.ToString(CultureInfo.InvariantCulture);

    public static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    public static string? NormalizeOptional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : NormalizeRequired(value, maxLength);

    public static string NormalizeRequired(string value, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
