#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CommandParameterManifestEntry
{
    public string Name { get; init; } = string.Empty;

    public int Order { get; init; }

    public bool Primary { get; init; }

    public bool Required { get; init; }

    public string ValueType { get; init; } = string.Empty;

    public string ValueHint { get; init; } = "value";

    public string? ContractDefault { get; init; }

    public double Minimum { get; init; } = double.NaN;
}

public sealed class CommandManifestEntry
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();

    public string? Category { get; init; }

    public string Usage { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string? Version { get; init; }

    public IReadOnlyList<CommandParameterManifestEntry> Parameters { get; init; } =
        Array.Empty<CommandParameterManifestEntry>();
}

public sealed class RuntimeCommandManifest
{
    public string ProviderId { get; init; } = string.Empty;

    public string? Version { get; init; }

    public IReadOnlyList<CommandManifestEntry> Commands { get; init; } =
        Array.Empty<CommandManifestEntry>();

    public static RuntimeCommandManifest Create(
        string providerId,
        string? version,
        IEnumerable<IRuntimeCommand> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(commands);

        var entries = commands
            .OrderBy(command => command.Schema.Name, StringComparer.OrdinalIgnoreCase)
            .Select(command => new CommandManifestEntry
            {
                Name = command.Schema.Name,
                Path = command.Schema.Path,
                Category = command.Schema.Category,
                Usage = command.Schema.Usage,
                ProviderId = providerId,
                Version = version,
                Parameters = command.Schema.Parameters
                    .OrderBy(param => param.Order)
                    .Select(param => new CommandParameterManifestEntry
                    {
                        Name = param.Name,
                        Order = param.Order,
                        Primary = param.Primary,
                        Required = param.Required,
                        ValueType = param.ValueType.Name,
                        ValueHint = param.ValueHint,
                        ContractDefault = param.ContractDefault?.ToString(),
                        Minimum = param.Minimum,
                    })
                    .ToArray(),
            })
            .ToArray();

        return new RuntimeCommandManifest
        {
            ProviderId = providerId,
            Version = version,
            Commands = new ReadOnlyCollection<CommandManifestEntry>(entries),
        };
    }

    public static RuntimeCommandManifest FromCatalog(
        RuntimeCommandCatalog catalog,
        string providerId,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Create(providerId, version, catalog.Commands);
    }

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson(bool writeIndented = true) =>
        JsonSerializer.Serialize(this, writeIndented ? IndentedJsonOptions : CompactJsonOptions);

    public static RuntimeCommandManifest FromJson(string json) =>
        JsonSerializer.Deserialize<RuntimeCommandManifest>(json) ??
        throw new InvalidOperationException("Failed to deserialize RuntimeCommandManifest.");
}

public static class RuntimeCommandManifestMerger
{
    public static RuntimeCommandManifest Merge(
        string targetProviderId,
        string? targetVersion,
        IEnumerable<RuntimeCommandManifest> manifests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProviderId);
        ArgumentNullException.ThrowIfNull(manifests);

        var mergedCommands = new Dictionary<string, CommandManifestEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in manifests.OrderBy(m => m.ProviderId, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var command in manifest.Commands)
            {
                if (mergedCommands.TryGetValue(command.Name, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Duplicate command '{command.Name}' found across providers '{existing.ProviderId}' and '{manifest.ProviderId}'.");
                }

                mergedCommands.Add(command.Name, command);
            }
        }

        var orderedCommands = mergedCommands.Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RuntimeCommandManifest
        {
            ProviderId = targetProviderId,
            Version = targetVersion,
            Commands = new ReadOnlyCollection<CommandManifestEntry>(orderedCommands),
        };
    }
}
