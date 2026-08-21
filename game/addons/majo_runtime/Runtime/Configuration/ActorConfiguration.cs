using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorConfiguration : Resource
{
    [Export]
    public Godot.Collections.Array<ActorDefinitionConfiguration> Actors { get; set; } = new();
}
