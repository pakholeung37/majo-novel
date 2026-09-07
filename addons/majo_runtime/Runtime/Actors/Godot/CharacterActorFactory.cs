#nullable enable

using System;
using Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Direction.Transitions;

using Majo.Runtime.Timing;

namespace Majo.Runtime.Actors.Godot;

public sealed class CharacterActorFactory : IActorFactory
{
    private readonly Node2D host;
    private readonly IOperationRunner runner;
    private readonly IActorTextureLoader textureLoader;
    private readonly CharacterPositionMap positions;
    private readonly IEasingRegistry? easings;
    private readonly ITransitionDriverRegistry? transitions;

    public CharacterActorFactory(
        Node2D host,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        IEasingRegistry? easings = null, ITransitionDriverRegistry? transitions = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        this.easings = easings;
        this.transitions = transitions;
    }

    public string FactoryId => ActorFactoryIds.Character;

    public System.Collections.Generic.IReadOnlyList<Majo.Runtime.Resources.ResourcePreloadEntry> GetPreloadResources(
        ActorDefinition definition, string appearance) => new[]
        {
            Majo.Runtime.Resources.ResourcePreloadEntry.Create<Texture2D>(
                definition.GetAppearanceResourceKey(appearance), definition.GetAppearanceOverride(appearance)),
        };

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public IActor Create(ActorDefinition definition)
    {
        var view = new Sprite2D { Name = definition.Id, Centered = true };
        host.AddChild(view);
        try
        {
            return new CharacterActor(
                definition, view, runner, textureLoader, positions, easings, transitions);
        }
        catch
        {
            view.QueueFree();
            throw;
        }
    }
}
