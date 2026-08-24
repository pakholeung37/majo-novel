#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Majo.Runtime.State;

public sealed class CanonicalJsonStateSerializer : IStateSerializer
{
    private readonly StateMigrationRegistry migrations;

    public CanonicalJsonStateSerializer(StateMigrationRegistry? migrations = null)
    {
        this.migrations = migrations ?? new StateMigrationRegistry();
    }

    public byte[] Serialize(StateEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.FormatVersion != StateEnvelope.CurrentFormatVersion)
        {
            throw new StateCompatibilityException(
                $"Cannot serialize state envelope format v{envelope.FormatVersion}.");
        }
        var payload = WritePayload(envelope);
        using (var document = JsonDocument.Parse(payload))
        {
            payload = Canonicalize(document.RootElement);
        }
        var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("checksum", checksum);
        writer.WritePropertyName("payload");
        writer.WriteRawValue(payload, skipInputValidation: false);
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    public StateEnvelope Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new StateEnvelopeException("State envelope is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            EnsureObject(root, "root");
            var checksum = RequiredString(root, "checksum");
            var payload = Required(root, "payload");
            var canonicalPayload = Canonicalize(payload);
            var actual = Convert.ToHexString(SHA256.HashData(canonicalPayload)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(checksum),
                    System.Text.Encoding.ASCII.GetBytes(actual)))
            {
                throw new StateIntegrityException("State envelope checksum does not match its payload.");
            }

            var migrated = migrations.MigrateEnvelope(payload);
            return ParseEnvelope(migrated.Value, migrated.Chain);
        }
        catch (StateRuntimeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new StateEnvelopeException("State envelope has an invalid shape.", exception);
        }
    }

    private static byte[] WritePayload(StateEnvelope envelope)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", envelope.FormatVersion);
        writer.WriteString("runtimeVersion", envelope.RuntimeVersion);
        writer.WriteString("gameBuildId", envelope.GameBuildId);
        writer.WriteString("storageNamespace", envelope.StorageNamespace);
        writer.WriteString("createdAt", FormatTime(envelope.CreatedAt));
        writer.WriteString("scope", envelope.Scope.ToString());
        writer.WritePropertyName("snapshot");
        WriteSnapshot(writer, envelope.Snapshot);
        writer.WritePropertyName("records");
        writer.WriteStartArray();
        foreach (var record in envelope.Records.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("key", record.Key);
            writer.WriteNumber("schemaVersion", record.SchemaVersion);
            writer.WriteBoolean("supportsPersistence", record.Capability.SupportsPersistence);
            writer.WriteString("rollbackPolicy", record.Capability.RollbackPolicy.ToString());
            writer.WriteString(
                "restoreRecordPolicy",
                record.Capability.RestoreRecordPolicy.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("script");
        if (envelope.Script is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("projectId", envelope.Script.ProjectId);
            writer.WriteString("nodeName", envelope.Script.NodeName);
            writer.WriteString("lineId", envelope.Script.LineId);
            writer.WriteString("programFingerprint", envelope.Script.ProgramFingerprint);
            writer.WriteEndObject();
        }
        writer.WritePropertyName("displayMetadata");
        writer.WriteStartObject();
        foreach (var pair in envelope.DisplayMetadata.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }
        writer.WriteEndObject();
        writer.WritePropertyName("migrationChain");
        writer.WriteStartArray();
        foreach (var step in envelope.MigrationChain)
        {
            writer.WriteStringValue(step);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, StateSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", snapshot.FormatVersion);
        writer.WriteString("capturedAt", FormatTime(snapshot.CapturedAt));
        writer.WritePropertyName("records");
        writer.WriteStartArray();
        foreach (var record in snapshot.Records.Values.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("key", record.Key);
            writer.WriteString("scope", record.Scope.ToString());
            writer.WriteNumber("schemaVersion", record.SchemaVersion);
            writer.WritePropertyName("payload");
            WriteCanonicalElement(writer, record.Payload);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static StateEnvelope ParseEnvelope(
        JsonElement payload,
        IReadOnlyList<string> appliedMigrations)
    {
        EnsureObject(payload, "payload");
        var scope = ParseEnum<StateScope>(RequiredString(payload, "scope"), "scope");
        var snapshotElement = Required(payload, "snapshot");
        var snapshotRecords = new List<StateRecord>();
        foreach (var record in RequiredArray(snapshotElement, "records").EnumerateArray())
        {
            snapshotRecords.Add(new StateRecord(
                RequiredString(record, "key"),
                ParseEnum<StateScope>(RequiredString(record, "scope"), "record scope"),
                RequiredInt(record, "schemaVersion"),
                Required(record, "payload")));
        }

        var snapshot = new StateSnapshot(
            RequiredInt(snapshotElement, "formatVersion"),
            ParseTime(RequiredString(snapshotElement, "capturedAt")),
            snapshotRecords);
        var metadata = new List<StateEnvelopeRecordMetadata>();
        foreach (var record in RequiredArray(payload, "records").EnumerateArray())
        {
            metadata.Add(new StateEnvelopeRecordMetadata(
                RequiredString(record, "key"),
                RequiredInt(record, "schemaVersion"),
                new StateContributorCapability(
                    RequiredBool(record, "supportsPersistence"),
                    ParseEnum<StateRollbackPolicy>(
                        RequiredString(record, "rollbackPolicy"),
                        "rollbackPolicy"),
                    ParseEnum<StateRestoreRecordPolicy>(
                        RequiredString(record, "restoreRecordPolicy"),
                        "restoreRecordPolicy"))));
        }

        StateScriptSummary? script = null;
        var scriptElement = Required(payload, "script");
        if (scriptElement.ValueKind != JsonValueKind.Null)
        {
            script = new StateScriptSummary(
                RequiredString(scriptElement, "projectId"),
                RequiredString(scriptElement, "nodeName"),
                RequiredString(scriptElement, "lineId"),
                RequiredString(scriptElement, "programFingerprint"));
        }

        var display = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in Required(payload, "displayMetadata").EnumerateObject())
        {
            display.Add(property.Name, property.Value.GetString() ?? throw new StateEnvelopeException(
                $"Display metadata '{property.Name}' must be a string."));
        }

        var chain = RequiredArray(payload, "migrationChain")
            .EnumerateArray()
            .Select(value => value.GetString() ?? throw new StateEnvelopeException(
                "Migration chain values must be strings."))
            .Concat(appliedMigrations)
            .ToArray();
        return new StateEnvelope(
            RequiredInt(payload, "formatVersion"),
            RequiredString(payload, "runtimeVersion"),
            RequiredString(payload, "gameBuildId"),
            RequiredString(payload, "storageNamespace"),
            ParseTime(RequiredString(payload, "createdAt")),
            scope,
            snapshot,
            metadata,
            script,
            display,
            chain);
    }

    private static byte[] Canonicalize(JsonElement element)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, WriterOptions);
        WriteCanonicalElement(writer, element);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new StateEnvelopeException(
                    $"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    private static JsonElement Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property)
            ? property
            : throw new StateEnvelopeException($"State envelope is missing '{name}'.");

    private static JsonElement RequiredArray(JsonElement value, string name)
    {
        var property = Required(value, name);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new StateEnvelopeException($"State envelope '{name}' must be an array.");
        }
        return property;
    }

    private static string RequiredString(JsonElement value, string name) =>
        Required(value, name).GetString() is { Length: > 0 } result
            ? result
            : throw new StateEnvelopeException($"State envelope '{name}' must be a string.");

    private static int RequiredInt(JsonElement value, string name) =>
        Required(value, name).TryGetInt32(out var result)
            ? result
            : throw new StateEnvelopeException($"State envelope '{name}' must be an integer.");

    private static bool RequiredBool(JsonElement value, string name)
    {
        var property = Required(value, name);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new StateEnvelopeException(
                $"State envelope '{name}' must be a boolean."),
        };
    }

    private static void EnsureObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new StateEnvelopeException($"State envelope '{name}' must be an object.");
        }
    }

    private static T ParseEnum<T>(string value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var result) && Enum.IsDefined(result)
            ? result
            : throw new StateEnvelopeException(
                $"State envelope '{name}' has unsupported value '{value}'.");

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };
}
