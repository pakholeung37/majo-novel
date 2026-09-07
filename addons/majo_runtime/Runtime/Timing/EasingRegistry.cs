#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Majo.Runtime.Timing;

public sealed class EasingRegistry : IEasingRegistry
{
    private readonly Dictionary<EasingId, EasingDefinition> definitions = new();
    private readonly Dictionary<string, EasingId> aliasToId = new(StringComparer.OrdinalIgnoreCase);
    private bool isSealed;

    public void Register(EasingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (isSealed)
        {
            throw new InvalidOperationException("Cannot register easing after registry is sealed.");
        }

        definition.Validate();

        if (definitions.ContainsKey(definition.Id))
        {
            throw new ArgumentException(
                $"Easing with ID '{definition.Id}' is already registered.",
                nameof(definition));
        }

        if (aliasToId.TryGetValue(definition.Id.Value, out var conflictingId))
        {
            throw new ArgumentException(
                $"Easing ID '{definition.Id}' conflicts with existing alias for '{conflictingId}'.",
                nameof(definition));
        }

        if (definition.Aliases is not null)
        {
            foreach (var rawAlias in definition.Aliases)
            {
                if (string.IsNullOrWhiteSpace(rawAlias))
                {
                    continue;
                }

                var alias = rawAlias.Trim().ToLowerInvariant();
                if (definitions.ContainsKey(new EasingId(alias)))
                {
                    throw new ArgumentException(
                        $"Alias '{rawAlias}' conflicts with existing easing ID '{alias}'.",
                        nameof(definition));
                }

                if (aliasToId.TryGetValue(alias, out var existing))
                {
                    throw new ArgumentException(
                        $"Alias '{rawAlias}' conflicts with existing alias for '{existing}'.",
                        nameof(definition));
                }
            }
        }

        definitions[definition.Id] = definition;
        aliasToId[definition.Id.Value] = definition.Id;

        if (definition.Aliases is not null)
        {
            foreach (var rawAlias in definition.Aliases)
            {
                if (string.IsNullOrWhiteSpace(rawAlias))
                {
                    continue;
                }

                var alias = rawAlias.Trim().ToLowerInvariant();
                aliasToId[alias] = definition.Id;
            }
        }
    }

    public bool TryGet(EasingId id, out EasingDefinition? definition)
    {
        if (id.IsEmpty)
        {
            definition = null;
            return false;
        }

        return definitions.TryGetValue(id, out definition);
    }

    public EasingDefinition GetRequired(EasingId id)
    {
        if (TryGet(id, out var definition) && definition is not null)
        {
            return definition;
        }

        var available = string.Join(", ", definitions.Keys.Select(k => k.Value));
        throw new KeyNotFoundException(
            $"Easing '{id}' is not registered. Available easings: [{available}].");
    }

    public EasingDefinition Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Easing name cannot be empty.", nameof(name));
        }

        var trimmed = name.Trim().ToLowerInvariant();
        var id = new EasingId(trimmed);

        if (definitions.TryGetValue(id, out var definition))
        {
            return definition;
        }

        if (aliasToId.TryGetValue(trimmed, out var mappedId) &&
            definitions.TryGetValue(mappedId, out definition))
        {
            return definition;
        }

        // Also check dash-stripped alias (e.g. "sine-in" vs "sinein")
        var dashless = trimmed.Replace("-", string.Empty);
        if (aliasToId.TryGetValue(dashless, out var dashlessId) &&
            definitions.TryGetValue(dashlessId, out definition))
        {
            return definition;
        }

        var available = string.Join(", ", definitions.Keys.Select(k => k.Value));
        throw new KeyNotFoundException(
            $"Easing '{name}' could not be resolved. Available easings: [{available}].");
    }

    public bool Contains(EasingId id) => !id.IsEmpty && definitions.ContainsKey(id);

    public void Seal() => isSealed = true;

    public Task InitializeAsync(System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
