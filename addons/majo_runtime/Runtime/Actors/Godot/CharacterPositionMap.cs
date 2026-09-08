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

    public Vector2 GetScenePosition(Vector2 scenePosition) =>
        StageCoordinates.ToPixels(scenePosition, configuration.ReferenceResolution) + GetPixelOffset();

    public Vector2 GetScenePercent(Vector2 pixels) =>
        StageCoordinates.ToPercent(pixels - GetPixelOffset(), configuration.ReferenceResolution);

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
