#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Majo.Runtime.Actors;

public class ActorDefinition
{
    public ActorDefinition(string id, string factoryId, object? factoryData = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryId);
        Id = id;
        FactoryId = factoryId;
        FactoryData = factoryData;
    }

    public ActorDefinition(
        string id,
        string factoryId,
        string defaultAppearance,
        IEnumerable<KeyValuePair<string, string>> appearances,
        CharacterPositionPreset initialPosition = CharacterPositionPreset.Center)
        : this(
            id,
            factoryId,
            new TextureActorDefinitionData(
                id, defaultAppearance, appearances, initialPosition))
    {
    }

    public string Id { get; }

    public string FactoryId { get; }

    public object? FactoryData { get; }

    public string DefaultAppearance =>
        GetFactoryData<TextureActorDefinitionData>().DefaultAppearance;

    public CharacterPositionPreset InitialPosition =>
        GetFactoryData<TextureActorDefinitionData>().InitialPosition;

    public IReadOnlyDictionary<string, string> Appearances =>
        GetFactoryData<TextureActorDefinitionData>().Appearances;

    public TData GetFactoryData<TData>() where TData : class =>
        FactoryData as TData ?? throw new ActorDefinitionException(
            Id,
            $"Factory '{FactoryId}' requires definition data '{typeof(TData).FullName}'.");

    public bool TryGetFactoryData<TData>(out TData? data) where TData : class
    {
        data = FactoryData as TData;
        return data is not null;
    }

    public string GetAppearancePath(string appearance) =>
        GetFactoryData<TextureActorDefinitionData>().GetAppearancePath(Id, appearance);
}

public sealed class TextureActorDefinitionData
{
    public TextureActorDefinitionData(
        string actorId,
        string defaultAppearance,
        IEnumerable<KeyValuePair<string, string>> appearances,
        CharacterPositionPreset initialPosition = CharacterPositionPreset.Center)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultAppearance);
        ArgumentNullException.ThrowIfNull(appearances);

        var appearanceMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var appearance in appearances)
        {
            if (string.IsNullOrWhiteSpace(appearance.Key))
            {
                throw new ActorDefinitionException(actorId, "Appearance ID is required.");
            }

            if (string.IsNullOrWhiteSpace(appearance.Value))
            {
                throw new ActorDefinitionException(
                    actorId, $"Appearance '{appearance.Key}' requires a resource path.");
            }

            if (!appearanceMap.TryAdd(appearance.Key, appearance.Value))
            {
                throw new ActorDefinitionException(
                    actorId, $"Appearance ID '{appearance.Key}' is duplicated.");
            }
        }

        if (appearanceMap.Count == 0)
        {
            throw new ActorDefinitionException(actorId, "At least one appearance is required.");
        }

        if (!appearanceMap.ContainsKey(defaultAppearance))
        {
            throw new ActorDefinitionException(
                actorId, $"Default appearance '{defaultAppearance}' is not defined.");
        }

        DefaultAppearance = defaultAppearance;
        InitialPosition = initialPosition;
        Appearances = new ReadOnlyDictionary<string, string>(appearanceMap);
    }

    public string DefaultAppearance { get; }

    public CharacterPositionPreset InitialPosition { get; }

    public IReadOnlyDictionary<string, string> Appearances { get; }

    public string GetAppearancePath(string actorId, string appearance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        if (!Appearances.TryGetValue(appearance, out var path))
        {
            throw new UnknownActorAppearanceException(actorId, appearance);
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
