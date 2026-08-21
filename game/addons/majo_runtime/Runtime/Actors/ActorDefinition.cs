#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Majo.Runtime.Actors;

public sealed class ActorDefinition
{
    public ActorDefinition(
        string id,
        string factoryId,
        string defaultAppearance,
        IEnumerable<KeyValuePair<string, string>> appearances,
        CharacterPositionPreset initialPosition = CharacterPositionPreset.Center)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultAppearance);
        ArgumentNullException.ThrowIfNull(appearances);

        var appearanceMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var appearance in appearances)
        {
            if (string.IsNullOrWhiteSpace(appearance.Key))
            {
                throw new ActorDefinitionException(id, "Appearance ID is required.");
            }

            if (string.IsNullOrWhiteSpace(appearance.Value))
            {
                throw new ActorDefinitionException(
                    id, $"Appearance '{appearance.Key}' requires a resource path.");
            }

            if (!appearanceMap.TryAdd(appearance.Key, appearance.Value))
            {
                throw new ActorDefinitionException(
                    id, $"Appearance ID '{appearance.Key}' is duplicated.");
            }
        }

        if (appearanceMap.Count == 0)
        {
            throw new ActorDefinitionException(id, "At least one appearance is required.");
        }

        if (!appearanceMap.ContainsKey(defaultAppearance))
        {
            throw new ActorDefinitionException(
                id, $"Default appearance '{defaultAppearance}' is not defined.");
        }

        Id = id;
        FactoryId = factoryId;
        DefaultAppearance = defaultAppearance;
        InitialPosition = initialPosition;
        Appearances = new ReadOnlyDictionary<string, string>(appearanceMap);
    }

    public string Id { get; }

    public string FactoryId { get; }

    public string DefaultAppearance { get; }

    public CharacterPositionPreset InitialPosition { get; }

    public IReadOnlyDictionary<string, string> Appearances { get; }

    public string GetAppearancePath(string appearance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        if (!Appearances.TryGetValue(appearance, out var path))
        {
            throw new UnknownActorAppearanceException(Id, appearance);
        }

        return path;
    }
}

public enum CharacterPositionPreset
{
    Left,
    Center,
    Right,
}
