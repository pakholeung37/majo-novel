using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorAppearanceConfiguration : Resource
{
    [Export]
    public string Id { get; set; } = string.Empty;

    [Export(PropertyHint.File)]
    public string TexturePath { get; set; } = string.Empty;
}
