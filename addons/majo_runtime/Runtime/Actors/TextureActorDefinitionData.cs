#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors;

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

