#nullable enable

using System;

namespace Majo.Runtime.Actors;

/// <summary>
/// Describes the minimum information required to resolve or create an actor.
/// Explicit actor definitions override these conventional defaults.
/// </summary>
public sealed record ActorSpawnRequest
{
    public ActorSpawnRequest(
        string actorId,
        string fallbackFactoryId,
        string initialAppearance,
        CharacterPositionPreset? initialPosition = null,
        string? requiredFactoryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackFactoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialAppearance);
        if (requiredFactoryId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requiredFactoryId);
        }
        ActorId = actorId.Trim();
        FallbackFactoryId = fallbackFactoryId.Trim();
        InitialAppearance = initialAppearance.Trim();
        InitialPosition = initialPosition;
        RequiredFactoryId = requiredFactoryId?.Trim();
    }

    public string ActorId { get; }

    /// <summary>
    /// Factory used only when no explicit actor definition exists.
    /// </summary>
    public string FallbackFactoryId { get; }

    public string InitialAppearance { get; }

    public CharacterPositionPreset? InitialPosition { get; }

    /// <summary>
    /// Optional exact factory constraint used by state restoration. Script commands
    /// normally rely on their expected actor interface instead.
    /// </summary>
    public string? RequiredFactoryId { get; }
}

/// <summary>
/// Resource conventions used when a built-in actor has no explicit definition.
/// </summary>
public sealed record ActorCreationConventions(
    string CharactersPath = "characters",
    string BackgroundsPath = "backgrounds");
