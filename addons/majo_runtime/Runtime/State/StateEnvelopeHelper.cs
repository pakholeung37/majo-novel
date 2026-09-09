#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.State;

internal static class StateEnvelopeHelper
{
    public static StateEnvelope CreateEnvelope(
        StateSnapshot snapshot,
        StateScope scope,
        IReadOnlyList<IStateContributor> contributors,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        IReadOnlyDictionary<string, string>? displayMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var durable = contributors
            .Where(value => value.Scope == scope && value.Capability.SupportsPersistence)
            .ToDictionary(value => value.Key, StringComparer.Ordinal);

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

    public static void ValidateSnapshotFormat(StateSnapshot snapshot)
    {
        if (snapshot.FormatVersion != StateSnapshot.CurrentFormatVersion)
        {
            throw new StateCompatibilityException(
                $"Unsupported state snapshot format '{snapshot.FormatVersion}'; " +
                $"expected '{StateSnapshot.CurrentFormatVersion}'.");
        }
    }

    public static void ValidateRecord(IStateContributor contributor, StateRecord record)
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

    public static string FormatMigrationChain(IReadOnlyList<string> chain) =>
        chain.Count == 0 ? "<none>" : string.Join(" -> ", chain);
}
