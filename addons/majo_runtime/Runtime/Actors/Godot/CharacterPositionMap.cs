using System;
using Godot;
using Majo.Runtime.Configuration;

namespace Majo.Runtime.Actors.Godot;

public sealed class CharacterPositionMap
{
    private readonly ActorConfiguration configuration;

    public CharacterPositionMap(ActorConfiguration configuration)
    {
        this.configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
    }

    public Vector2 Get(CharacterPositionPreset preset)
    {
        var position = preset switch
        {
            CharacterPositionPreset.Left => configuration.LeftPosition,
            CharacterPositionPreset.Center => configuration.CenterPosition,
            CharacterPositionPreset.Right => configuration.RightPosition,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };
        return position + GetPixelOffset();
    }

    public Vector2 GetScenePosition(Vector2 scenePosition)
    {
        if (!float.IsFinite(scenePosition.X) || !float.IsFinite(scenePosition.Y) ||
            scenePosition.X is < 0 or > 100 || scenePosition.Y is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scenePosition), "Scene position percentages must be between 0 and 100.");
        }

        var resolution = configuration.ReferenceResolution;
        if (resolution.X <= 0 || resolution.Y <= 0)
        {
            throw new ActorException(
                "Scene-position commands require a positive actor reference resolution.");
        }

        return new Vector2(
            resolution.X * scenePosition.X / 100f,
            resolution.Y * (1f - scenePosition.Y / 100f)) + GetPixelOffset();
    }

    private Vector2 GetPixelOffset()
    {
        var offset = configuration.ScenePositionOffset;
        if (offset == Vector2.Zero)
        {
            return Vector2.Zero;
        }

        var resolution = configuration.ReferenceResolution;
        if (resolution.X <= 0 || resolution.Y <= 0)
        {
            throw new ActorException(
                "Scene-position offset requires a positive actor reference resolution.");
        }

        return new Vector2(
            resolution.X * offset.X / 100f,
            -resolution.Y * offset.Y / 100f);
    }
}
