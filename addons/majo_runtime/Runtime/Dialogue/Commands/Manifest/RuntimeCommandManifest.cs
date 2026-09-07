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

    public double? Minimum { get; init; }

    public CommandValueDescription? Description { get; init; }

    public bool HasContractDefault { get; init; }

    public bool DefaultIsInputText { get; init; }
}

public sealed class CommandParameterGroupManifestEntry
{
    public string Id { get; init; } = string.Empty;

    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();
}

public sealed class CommandParameterSetManifestEntry
{
    public CommandParameterSetRule Rule { get; init; }

    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();
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

    public IReadOnlyList<CommandParameterGroupManifestEntry> ParameterGroups { get; init; } =
        Array.Empty<CommandParameterGroupManifestEntry>();

    public IReadOnlyList<CommandParameterSetManifestEntry> ParameterSets { get; init; } =
        Array.Empty<CommandParameterSetManifestEntry>();
}

public sealed class RuntimeCommandManifest
{
    public int FormatVersion { get; init; } = 2;
    public string ProviderId { get; init; } = string.Empty;

    public string? Version { get; init; }

    public IReadOnlyList<CommandManifestEntry> Commands { get; init; } =
        Array.Empty<CommandManifestEntry>();

    public static RuntimeCommandManifest Create(
        string providerId,
        string? version,
        IEnumerable<IBoundCommand> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(commands);

        var entries = commands
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(command => CreateCommandManifestEntry(command, providerId, version))
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
        var entries = catalog.Entries
            .Select(entry => CreateCommandManifestEntry(
                entry.Command,
                string.IsNullOrWhiteSpace(entry.ProviderId) ? providerId : entry.ProviderId,
                entry.Version ?? version))
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RuntimeCommandManifest
        {
            ProviderId = providerId,
            Version = version,
            Commands = new ReadOnlyCollection<CommandManifestEntry>(entries),
        };
    }

    private static CommandManifestEntry CreateCommandManifestEntry(
        IBoundCommand command,
        string providerId,
        string? version)
    {
        var definition = command.Definition;
        return new CommandManifestEntry
        {
            Name = definition.Name,
            Path = definition.Path,
            Category = definition.Category,
            Usage = command.Usage,
            ProviderId = providerId,
            Version = version,
            Parameters = command.Slots
                .OrderBy(slot => slot.Declaration.Order)
                .Select(slot => CreateParameterEntry(slot))
                .ToArray(),
            ParameterGroups = FlattenGroups(definition.Groups)
                .Select(group => new CommandParameterGroupManifestEntry
                {
                    Id = group.Name,
                    Parameters = FlattenGroupParameters(group).Select(p => p.Name).ToArray(),
                })
                .OrderBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ParameterSets = CommandContractValidation.ResolveSets(definition)
                .Select(set => new CommandParameterSetManifestEntry
                {
                    Rule = set.Rule,
                    Parameters = set.Parameters.ToArray(),
                })
                .ToArray(),
        };
    }

    private static CommandParameterManifestEntry CreateParameterEntry(BoundParameterSlot slot)
    {
        var param = slot.Declaration;
        return new CommandParameterManifestEntry
        {
            Name = param.Name,
            Order = param.Order,
            Primary = param.Primary,
            Required = param.Required,
            ValueType = param.ValueType.FullName ?? param.ValueType.Name,
            ValueHint = slot.Description?.Syntax ?? param.ValueHint,
            Description = slot.Description,
            HasContractDefault = slot.HasContractDefault,
            DefaultIsInputText = slot.HasContractDefault && slot.Descriptor is null,
            ContractDefault = slot.HasContractDefault
                ? slot.FormattedDefault : null,
            Minimum = slot.Minimum,
        };
    }

    private static IEnumerable<CommandGroupDeclaration> FlattenGroups(IEnumerable<CommandGroupDeclaration> groups) =>
        groups.SelectMany(g => new[] { g }.Concat(FlattenGroups(g.SubGroups ?? Array.Empty<CommandGroupDeclaration>())));

    private static IEnumerable<CommandParameterDeclaration> FlattenGroupParameters(CommandGroupDeclaration group) =>
        (group.Parameters ?? Array.Empty<CommandParameterDeclaration>()).Concat(
            (group.SubGroups ?? Array.Empty<CommandGroupDeclaration>()).SelectMany(FlattenGroupParameters));

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
        ReadVersioned(json);

    private static RuntimeCommandManifest ReadVersioned(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(FormatVersion), out var version) || version.GetInt32() != 2)
            throw new InvalidOperationException("Only command manifest format version 2 is supported.");
        return JsonSerializer.Deserialize<RuntimeCommandManifest>(json) ?? throw new InvalidOperationException("Invalid command manifest.");
    }
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
