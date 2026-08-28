using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorConfiguration : Resource
{
    [Export]
    public Vector2I ReferenceResolution { get; set; } = Vector2I.Zero;

    /// <summary>
    /// Global character baseline adjustment in scene percentages. Positive Y moves
    /// characters upward, matching Naninovel scene-position coordinates.
    /// </summary>
    [Export]
    public Vector2 ScenePositionOffset { get; set; } = Vector2.Zero;

    [Export]
    public Godot.Collections.Array<ActorDefinitionConfiguration> Actors { get; set; } = new();

    [Export]
    public Vector2 LeftPosition { get; set; } = new(320, 540);

    [Export]
    public Vector2 CenterPosition { get; set; } = new(640, 540);

    [Export]
    public Vector2 RightPosition { get; set; } = new(960, 540);
}
