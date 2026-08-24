#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace Majo.Runtime.State;

public enum StateScope
{
    Session,
    Game,
    Global,
    Settings,
}

public enum StateRollbackPolicy
{
    Supported,
    Barrier,
    Unsupported,
}

public enum StateRestoreRecordPolicy
{
    Required,
    UseDefault,
}

public sealed record StateContributorCapability(
    bool SupportsPersistence,
    StateRollbackPolicy RollbackPolicy,
    StateRestoreRecordPolicy RestoreRecordPolicy)
{
    public static StateContributorCapability Transient { get; } = new(
        false,
        StateRollbackPolicy.Unsupported,
        StateRestoreRecordPolicy.UseDefault);

    public static StateContributorCapability Durable { get; } = new(
        true,
        StateRollbackPolicy.Barrier,
        StateRestoreRecordPolicy.Required);

    public static StateContributorCapability DurableRollback { get; } = new(
        true,
        StateRollbackPolicy.Supported,
        StateRestoreRecordPolicy.Required);
}

public sealed record StateRecord
{
    public StateRecord(string key, StateScope scope, int schemaVersion, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        Key = key.Trim();
        Scope = scope;
        SchemaVersion = schemaVersion;
        Payload = payload.Clone();
    }

    public string Key { get; }

    public StateScope Scope { get; }

    public int SchemaVersion { get; }

    public JsonElement Payload { get; }
}

public sealed record StateSnapshot
{
    public const int CurrentFormatVersion = 1;

    public StateSnapshot(
        int formatVersion,
        DateTimeOffset capturedAt,
        IEnumerable<StateRecord> records)
    {
        if (formatVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        ArgumentNullException.ThrowIfNull(records);
        var map = new Dictionary<string, StateRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (!map.TryAdd(record.Key, record))
            {
                throw new StateSnapshotException(
                    $"State snapshot contains duplicate record '{record.Key}'.");
            }
        }

        FormatVersion = formatVersion;
        CapturedAt = capturedAt;
        Records = new ReadOnlyDictionary<string, StateRecord>(map);
    }

    public int FormatVersion { get; }

    public DateTimeOffset CapturedAt { get; }

    public IReadOnlyDictionary<string, StateRecord> Records { get; }
}

public sealed record StateScriptSummary(
    string ProjectId,
    string NodeName,
    string LineId,
    string ProgramFingerprint);

public sealed record StateEnvelopeRecordMetadata(
    string Key,
    int SchemaVersion,
    StateContributorCapability Capability);

public sealed record StateEnvelope
{
    public const int CurrentFormatVersion = 1;

    public StateEnvelope(
        int formatVersion,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        DateTimeOffset createdAt,
        StateScope scope,
        StateSnapshot snapshot,
        IEnumerable<StateEnvelopeRecordMetadata> records,
        StateScriptSummary? script = null,
        IReadOnlyDictionary<string, string>? displayMetadata = null,
        IEnumerable<string>? migrationChain = null)
    {
        if (formatVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageNamespace);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(records);
        var metadata = records.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
        if (metadata.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() !=
            metadata.Length)
        {
            throw new StateEnvelopeException("State envelope contains duplicate record metadata.");
        }

        if (snapshot.Records.Values.Any(value => value.Scope != scope))
        {
            throw new StateEnvelopeException(
                $"State envelope scope '{scope}' cannot contain records from another scope.");
        }

        var snapshotShape = snapshot.Records.Values
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => (value.Key, value.SchemaVersion))
            .ToArray();
        var metadataShape = metadata
            .Select(value => (value.Key, value.SchemaVersion))
            .ToArray();
        if (!snapshotShape.SequenceEqual(metadataShape))
        {
            throw new StateEnvelopeException(
                "State envelope record metadata does not match its snapshot records.");
        }

        FormatVersion = formatVersion;
        RuntimeVersion = runtimeVersion.Trim();
        GameBuildId = gameBuildId.Trim();
        StorageNamespace = storageNamespace.Trim();
        CreatedAt = createdAt;
        Scope = scope;
        Snapshot = snapshot;
        Records = Array.AsReadOnly(metadata);
        Script = script;
        DisplayMetadata = new ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(
                (displayMetadata ?? new Dictionary<string, string>())
                    .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
                StringComparer.Ordinal));
        MigrationChain = Array.AsReadOnly((migrationChain ?? Array.Empty<string>()).ToArray());
    }

    public int FormatVersion { get; }
    public string RuntimeVersion { get; }
    public string GameBuildId { get; }
    public string StorageNamespace { get; }
    public DateTimeOffset CreatedAt { get; }
    public StateScope Scope { get; }
    public StateSnapshot Snapshot { get; }
    public IReadOnlyList<StateEnvelopeRecordMetadata> Records { get; }
    public StateScriptSummary? Script { get; }
    public IReadOnlyDictionary<string, string> DisplayMetadata { get; }
    public IReadOnlyList<string> MigrationChain { get; }
}

public enum StateManagerStatus
{
    Idle,
    Capturing,
    Restoring,
    Faulted,
}

public sealed record StateStatusChanged(
    StateManagerStatus Previous,
    StateManagerStatus Current);

internal static class StateJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static JsonElement Serialize<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static T Deserialize<T>(StateRecord record)
    {
        try
        {
            return record.Payload.Deserialize<T>(Options) ?? throw new StateSnapshotException(
                $"State record '{record.Key}' has an empty payload.");
        }
        catch (StateSnapshotException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StateSnapshotException(
                $"State record '{record.Key}' has an invalid payload.",
                exception);
        }
    }
}
