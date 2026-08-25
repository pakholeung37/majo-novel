#nullable enable

using Godot;
using Majo.Runtime.Actors;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorDefinitionConfiguration : Resource
{
    [Export]
    public string Id { get; set; } = string.Empty;

    [Export]
    public string FactoryId { get; set; } = ActorFactoryIds.Character;

    [Export]
    public string DefaultAppearance { get; set; } = "default";

    [Export]
    public string ResourcePathPrefix { get; set; } = string.Empty;

    [Export]
    public CharacterPositionPreset InitialPosition { get; set; } =
        CharacterPositionPreset.Center;

    [Export]
    public Resource? FactoryConfiguration { get; set; }

    [Export]
    public Godot.Collections.Array<ActorAppearanceConfiguration> Appearances { get; set; } = new();
}
