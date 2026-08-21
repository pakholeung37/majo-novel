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

    public Vector2 Get(CharacterPositionPreset preset) => preset switch
    {
        CharacterPositionPreset.Left => configuration.LeftPosition,
        CharacterPositionPreset.Center => configuration.CenterPosition,
        CharacterPositionPreset.Right => configuration.RightPosition,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
    };
}
