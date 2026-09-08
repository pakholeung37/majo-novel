#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Majo.Runtime.Configuration;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors;

public class ActorDefinition
{
    public ActorDefinition(string id, string factoryId, object? factoryData = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryId);
        if (id is "move" or "fx" or "hide")
            throw new ActorDefinitionException(id, "This instance id is reserved by Direction child commands.");
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
                id,
                GetDefaultResourcePathPrefix(factoryId),
                defaultAppearance,
                appearances,
                initialPosition))
    {
    }

    public ActorDefinition(
        string id,
        string factoryId,
        string resourcePathPrefix,
        string defaultAppearance,
        IEnumerable<KeyValuePair<string, string>> appearanceOverrides,
        CharacterPositionPreset initialPosition = CharacterPositionPreset.Center)
        : this(
            id,
            factoryId,
            new TextureActorDefinitionData(
                id,
                resourcePathPrefix,
                defaultAppearance,
                appearanceOverrides,
                initialPosition))
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

    public ResourceKey GetAppearanceResourceKey(string appearance) =>
        GetFactoryData<TextureActorDefinitionData>().GetAppearanceResourceKey(Id, appearance);

    public string? GetAppearanceOverride(string appearance) =>
        GetFactoryData<TextureActorDefinitionData>().GetAppearanceOverride(appearance);

    private static string GetDefaultResourcePathPrefix(string factoryId) => factoryId switch
    {
        ActorFactoryIds.Character => ResourceConfiguration.DefaultCharactersPath,
        ActorFactoryIds.Background => ResourceConfiguration.DefaultBackgroundsPath,
        _ => factoryId,
    };
}

public sealed class TextureActorDefinitionData
{
    public TextureActorDefinitionData(
        string actorId,
        string resourcePathPrefix,
        string defaultAppearance,
        IEnumerable<KeyValuePair<string, string>> appearanceOverrides,
        CharacterPositionPreset initialPosition = CharacterPositionPreset.Center)
    {
        ResourcePathPrefix = new ResourceKey(resourcePathPrefix).Path;
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultAppearance);
        ArgumentNullException.ThrowIfNull(appearanceOverrides);

        var appearanceMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var appearance in appearanceOverrides)
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

        DefaultAppearance = defaultAppearance;
        InitialPosition = initialPosition;
        Appearances = new ReadOnlyDictionary<string, string>(appearanceMap);
    }

    public string DefaultAppearance { get; }

    public string ResourcePathPrefix { get; }

    public string? ResourceId { get; init; }
    public global::Godot.Vector2 Anchor { get; init; } = new(0.5f, 0.5f);

    public CharacterPositionPreset InitialPosition { get; }

    public IReadOnlyDictionary<string, string> Appearances { get; }

    public string GetAppearancePath(string actorId, string appearance)
    {
        return GetAppearanceOverride(appearance) ??
            GetAppearanceResourceKey(actorId, appearance).Path;
    }

    public ResourceKey GetAppearanceResourceKey(string actorId, string appearance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        return ResourceKey.Combine(ResourcePathPrefix, ResourceId ?? actorId, appearance);
    }

    public string? GetAppearanceOverride(string appearance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        return Appearances.TryGetValue(appearance, out var path) ? path : null;
    }
}

public enum CharacterPositionPreset
{
    Left,
    Center,
    Right,
}
