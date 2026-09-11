using System;
using Godot;
using Majo.Runtime.Configuration;

namespace Majo.Runtime.Actors.Godot;

public sealed class CharacterPositionMap
{
    private readonly Vector2 left;
    private readonly Vector2 center;
    private readonly Vector2 right;
    private readonly Vector2 offset;
    private readonly Vector2I resolution;

    public CharacterPositionMap(ActorConfiguration configuration)
        : this(ActorRenderSettings.Capture(configuration)) { }

    public CharacterPositionMap(ActorRenderSettings configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        left = configuration.LeftPosition;
        center = configuration.CenterPosition;
        right = configuration.RightPosition;
        offset = configuration.ScenePositionOffset;
        resolution = configuration.ReferenceResolution;
    }

    public Vector2 Get(CharacterPositionPreset preset)
    {
        var position = preset switch
        {
            CharacterPositionPreset.Left => left,
            CharacterPositionPreset.Center => center,
            CharacterPositionPreset.Right => right,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };
        return position + GetPixelOffset();
    }

    public Vector2 GetScenePosition(Vector2 scenePosition) =>
        StageCoordinates.ToPixels(scenePosition, resolution) + GetPixelOffset();

    public Vector2 GetScenePercent(Vector2 pixels) =>
        StageCoordinates.ToPercent(pixels - GetPixelOffset(), resolution);

    private Vector2 GetPixelOffset()
    {
        if (offset == Vector2.Zero)
        {
            return Vector2.Zero;
        }

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
