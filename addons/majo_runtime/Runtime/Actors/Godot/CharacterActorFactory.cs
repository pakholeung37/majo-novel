#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Actors.Godot;

public sealed class CharacterActorFactory : IActorFactory
{
    private readonly Node2D host;
    private readonly IActorTextureLoader textureLoader;
    private readonly CharacterPositionMap positions;
    private readonly IActorAppearanceTransitionService transitions;

    public CharacterActorFactory(
        Node2D host,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        IActorAppearanceTransitionService? transitions = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        this.transitions = transitions ?? new GodotActorAppearanceTransitionService();
    }

    public string FactoryId => ActorFactoryIds.Character;

    public string GetInitialAppearance(ActorDefinition definition) =>
        definition.GetFactoryData<TextureActorDefinitionData>().DefaultAppearance;

    public System.Collections.Generic.IReadOnlyList<Majo.Runtime.Resources.ResourcePreloadEntry> GetPreloadResources(
        ActorDefinition definition, string appearance) => new[]
        {
            Majo.Runtime.Resources.ResourcePreloadEntry.Create<Texture2D>(
                definition.GetFactoryData<TextureActorDefinitionData>().GetAppearanceResourceKey(
                    definition.DefinitionId, appearance),
                definition.GetFactoryData<TextureActorDefinitionData>().GetAppearanceOverride(appearance)),
        };

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = new Sprite2D { Name = definition.Id, Centered = true, Visible = false };
        host.AddChild(view);
        try
        {
            var actor = new TextureCharacterActor(
                definition, view, textureLoader, positions, transitions);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
        catch
        {
            view.QueueFree();
            throw;
        }
    }
}
