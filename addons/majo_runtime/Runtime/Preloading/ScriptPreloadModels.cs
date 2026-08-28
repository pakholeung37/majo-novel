#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Actors;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Preloading;

public sealed record ActorWarmupRequest
{
    public ActorWarmupRequest(
        string actorId,
        string fallbackFactoryId,
        IEnumerable<string> appearances,
        CharacterPositionPreset? initialPosition = null,
        string? requiredFactoryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackFactoryId);
        ArgumentNullException.ThrowIfNull(appearances);
        var normalizedAppearances = appearances
            .Select(appearance =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
                return appearance.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedAppearances.Length == 0)
        {
            throw new ArgumentException(
                "An actor warmup requires at least one appearance.",
                nameof(appearances));
        }

        ActorId = actorId.Trim();
        FallbackFactoryId = fallbackFactoryId.Trim();
        Appearances = normalizedAppearances;
        InitialPosition = initialPosition;
        RequiredFactoryId = requiredFactoryId?.Trim();
    }

    public string ActorId { get; }

    public string FallbackFactoryId { get; }

    public IReadOnlyList<string> Appearances { get; }

    public CharacterPositionPreset? InitialPosition { get; }

    public string? RequiredFactoryId { get; }
}

public sealed record ScriptPreloadWindow
{
    public ScriptPreloadWindow(
        string id,
        ResourcePreloadManifest? resources = null,
        IEnumerable<ActorWarmupRequest>? actors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id.Trim();
        Resources = resources;
        Actors = (actors ?? []).ToArray();
        if (Resources is null && Actors.Count == 0)
        {
            throw new ArgumentException(
                "A script preload window requires resources or actor warmups.");
        }
    }

    public string Id { get; }

    public ResourcePreloadManifest? Resources { get; }

    public IReadOnlyList<ActorWarmupRequest> Actors { get; }
}
