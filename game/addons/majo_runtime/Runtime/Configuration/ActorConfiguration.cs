using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorConfiguration : Resource
{
    [Export]
    public Godot.Collections.Array<ActorDefinitionConfiguration> Actors { get; set; } = new();

    [Export]
    public Vector2 LeftPosition { get; set; } = new(320, 540);

    [Export]
    public Vector2 CenterPosition { get; set; } = new(640, 540);

    [Export]
    public Vector2 RightPosition { get; set; } = new(960, 540);
}
