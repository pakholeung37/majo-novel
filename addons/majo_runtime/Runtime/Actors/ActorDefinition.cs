#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors;

public class ActorDefinition
{
    public ActorDefinition(
        string id,
        string factoryId,
        object? factoryData = null,
        string? definitionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryId);
        if (id is "move" or "fx" or "hide")
            throw new ActorDefinitionException(id, "This instance id is reserved by Direction child commands.");
        Id = id;
        DefinitionId = string.IsNullOrWhiteSpace(definitionId) ? id : definitionId.Trim();
        FactoryId = factoryId;
        FactoryData = factoryData;
    }

    public string Id { get; }

    public string DefinitionId { get; }

    public string FactoryId { get; }

    public object? FactoryData { get; }

    public TData GetFactoryData<TData>() where TData : class =>
        FactoryData as TData ?? throw new ActorDefinitionException(
            Id,
            $"Factory '{FactoryId}' requires definition data '{typeof(TData).FullName}'.");

    public bool TryGetFactoryData<TData>(out TData? data) where TData : class
    {
        data = FactoryData as TData;
        return data is not null;
    }

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

    public ResourceKey? DefaultResourceKey { get; init; }

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
        if (DefaultResourceKey is { } key)
            return appearance == DefaultAppearance ? key : ResourceKey.Combine(key.Path, appearance);
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
