#nullable enable

using System;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Dialogue.Commands;

public interface IDirectionResourceKeyResolver
{
    ResourceKey ResolveBackdrop(string semanticId);
    ResourceKey ResolveCg(string semanticId);
}

public sealed class DirectionResourceKeyResolver : IDirectionResourceKeyResolver
{
    private readonly ResourceConfiguration configuration;

    public DirectionResourceKeyResolver(ResourceConfiguration configuration)
    {
        this.configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
    }

    public ResourceKey ResolveBackdrop(string semanticId) => Resolve(semanticId);

    public ResourceKey ResolveCg(string semanticId) => Resolve(semanticId);

    private ResourceKey Resolve(string semanticId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticId);
        var normalized = semanticId.Trim().Replace('\\', '/').Trim('/');
        var root = configuration.BackgroundsPath.Trim().Replace('\\', '/').Trim('/');
        return normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? new ResourceKey(normalized)
            : ResourceKey.Combine(root, normalized);
    }
}

public interface IDirectionPositionResolver
{
    DirectionPoint Resolve(string value);
}

public sealed class DirectionPositionResolver : IDirectionPositionResolver
{
    private readonly ActorConfiguration configuration;

    public DirectionPositionResolver(ActorConfiguration configuration)
    {
        this.configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
    }

    public DirectionPoint Resolve(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (Enum.TryParse<CharacterPositionPreset>(normalized, true, out var preset))
            return FromPixels(new CharacterPositionMap(configuration).Get(preset));

        var parts = normalized.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var y) ||
            !float.IsFinite(x) || !float.IsFinite(y) ||
            x is < 0 or > 100 || y is < 0 or > 100)
        {
            throw new ArgumentException(
                $"Unknown mark '{value}'. Use left, center, right, or finite x,y percentages between 0 and 100.",
                nameof(value));
        }

        return new DirectionPoint(x, y);
    }

    private DirectionPoint FromPixels(Vector2 pixels)
    {
        var resolution = configuration.ReferenceResolution;
        if (resolution.X <= 0 || resolution.Y <= 0)
        {
            throw new ArgumentException(
                "Direction marks require a positive actor reference resolution.");
        }

        var offset = new Vector2(
            resolution.X * configuration.ScenePositionOffset.X / 100f,
            -resolution.Y * configuration.ScenePositionOffset.Y / 100f);
        var basePosition = pixels - offset;
        return new DirectionPoint(
            basePosition.X / resolution.X * 100f,
            (1f - basePosition.Y / resolution.Y) * 100f);
    }
}
