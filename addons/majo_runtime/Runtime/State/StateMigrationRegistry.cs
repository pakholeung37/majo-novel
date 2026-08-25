#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Majo.Runtime.State;

public interface IStateEnvelopeMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    string Id { get; }
    JsonElement Migrate(JsonElement payload);
}

public interface IStateContributorMigration
{
    string FromKey { get; }
    int FromSchemaVersion { get; }
    string ToKey { get; }
    int ToSchemaVersion { get; }
    string Id { get; }
    JsonElement Migrate(JsonElement payload);
}

public sealed record StateMigrationResult<T>(T Value, IReadOnlyList<string> Chain);

public sealed class StateMigrationRegistry
{
    private readonly Dictionary<int, IStateEnvelopeMigration> envelopeMigrations = new();
    private readonly Dictionary<(string Key, int Version), IStateContributorMigration>
        contributorMigrations = new();
    private readonly HashSet<string> removedContributors = new(StringComparer.Ordinal);

    public StateMigrationRegistry RegisterEnvelope(IStateEnvelopeMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ValidateStep(migration.FromVersion, migration.ToVersion, migration.Id);
        if (!envelopeMigrations.TryAdd(migration.FromVersion, migration))
        {
            throw new StateCompatibilityException(
                $"An envelope migration from v{migration.FromVersion} is already registered.");
        }

        return this;
    }

    public StateMigrationRegistry RegisterContributor(IStateContributorMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentException.ThrowIfNullOrWhiteSpace(migration.FromKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(migration.ToKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(migration.Id);
        if (migration.FromSchemaVersion <= 0 || migration.ToSchemaVersion <= 0 ||
            (string.Equals(migration.FromKey, migration.ToKey, StringComparison.Ordinal) &&
             migration.ToSchemaVersion <= migration.FromSchemaVersion) ||
            (!string.Equals(migration.FromKey, migration.ToKey, StringComparison.Ordinal) &&
             migration.ToSchemaVersion < migration.FromSchemaVersion))
        {
            throw new ArgumentException(
                "Contributor migrations must rename a key or move to a higher schema version.");
        }
        if (!contributorMigrations.TryAdd(
                (migration.FromKey, migration.FromSchemaVersion),
                migration))
        {
            throw new StateCompatibilityException(
                $"A contributor migration from '{migration.FromKey}' " +
                $"v{migration.FromSchemaVersion} is already registered.");
        }

        return this;
    }

    public StateMigrationRegistry RegisterRemovedContributor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        removedContributors.Add(key.Trim());
        return this;
    }

    internal StateMigrationResult<JsonElement> MigrateEnvelope(JsonElement payload)
    {
        var current = payload.Clone();
        var chain = new List<string>();
        var visited = new HashSet<int>();
        while (GetRequiredInt(current, "formatVersion") != StateEnvelope.CurrentFormatVersion)
        {
            var version = GetRequiredInt(current, "formatVersion");
            if (!visited.Add(version) || !envelopeMigrations.TryGetValue(version, out var migration))
            {
                throw new StateCompatibilityException(
                    $"Unsupported state envelope format v{version}; expected " +
                    $"v{StateEnvelope.CurrentFormatVersion}.");
            }

            current = migration.Migrate(current).Clone();
            if (GetRequiredInt(current, "formatVersion") != migration.ToVersion)
            {
                throw new StateCompatibilityException(
                    $"Envelope migration '{migration.Id}' did not produce v{migration.ToVersion}.");
            }

            chain.Add(migration.Id);
        }

        return new StateMigrationResult<JsonElement>(current, chain.AsReadOnly());
    }

    internal StateMigrationResult<IReadOnlyDictionary<string, StateRecord>> ResolveRecords(
        StateSnapshot snapshot,
        IReadOnlyList<IStateContributor> contributors,
        StateScope scope)
    {
        var expected = contributors
            .Where(value => value.Scope == scope && value.Capability.SupportsPersistence)
            .ToDictionary(value => value.Key, StringComparer.Ordinal);
        var resolved = new Dictionary<string, StateRecord>(StringComparer.Ordinal);
        var chain = new List<string>();

        foreach (var source in snapshot.Records.Values.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (source.Scope != scope)
            {
                throw new StateCompatibilityException(
                    $"Record '{source.Key}' has scope '{source.Scope}', expected '{scope}'.");
            }

            var current = source;
            var visited = new HashSet<(string Key, int Version)>();
            while (!expected.TryGetValue(current.Key, out var target) ||
                   current.SchemaVersion != target.SchemaVersion)
            {
                if (expected.ContainsKey(current.Key) &&
                    current.SchemaVersion > expected[current.Key].SchemaVersion)
                {
                    throw new StateCompatibilityException(
                        $"Record '{current.Key}' schema v{current.SchemaVersion} is newer than " +
                        $"runtime schema v{expected[current.Key].SchemaVersion}.");
                }

                var identity = (current.Key, current.SchemaVersion);
                if (!visited.Add(identity) ||
                    !contributorMigrations.TryGetValue(identity, out var migration))
                {
                    if (!expected.ContainsKey(current.Key) && removedContributors.Contains(current.Key))
                    {
                        chain.Add($"removed:{current.Key}@{current.SchemaVersion}");
                        current = null!;
                        break;
                    }

                    throw new StateCompatibilityException(
                        $"No migration is registered for state record '{current.Key}' " +
                        $"schema v{current.SchemaVersion}.");
                }

                current = new StateRecord(
                    migration.ToKey,
                    scope,
                    migration.ToSchemaVersion,
                    migration.Migrate(current.Payload));
                chain.Add(migration.Id);
            }

            if (current is not null && !resolved.TryAdd(current.Key, current))
            {
                throw new StateCompatibilityException(
                    $"State migrations produced duplicate record '{current.Key}'.");
            }
        }

        foreach (var contributor in expected.Values.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (!resolved.ContainsKey(contributor.Key) &&
                contributor.Capability.RestoreRecordPolicy == StateRestoreRecordPolicy.Required)
            {
                throw new StateCompatibilityException(
                    $"Required state record '{contributor.Key}' is missing.");
            }
        }

        return new StateMigrationResult<IReadOnlyDictionary<string, StateRecord>>(
            resolved,
            chain.AsReadOnly());
    }

    private static int GetRequiredInt(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || !property.TryGetInt32(out var result))
        {
            throw new StateEnvelopeException($"State envelope is missing integer '{name}'.");
        }

        return result;
    }

    private static void ValidateStep(int from, int to, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (from <= 0 || to <= from)
        {
            throw new ArgumentException("State migrations must move to a higher positive version.");
        }
    }
}

public sealed class DelegateStateContributorMigration : IStateContributorMigration
{
    private readonly Func<JsonElement, JsonElement> migrate;

    public DelegateStateContributorMigration(
        string id,
        string fromKey,
        int fromSchemaVersion,
        string toKey,
        int toSchemaVersion,
        Func<JsonElement, JsonElement> migrate)
    {
        Id = id;
        FromKey = fromKey;
        FromSchemaVersion = fromSchemaVersion;
        ToKey = toKey;
        ToSchemaVersion = toSchemaVersion;
        this.migrate = migrate ?? throw new ArgumentNullException(nameof(migrate));
    }

    public string FromKey { get; }
    public int FromSchemaVersion { get; }
    public string ToKey { get; }
    public int ToSchemaVersion { get; }
    public string Id { get; }
    public JsonElement Migrate(JsonElement payload) => migrate(payload).Clone();
}
