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
