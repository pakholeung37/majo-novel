#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Creates texture objects from definitions resolved by the actor manager.</summary>
public sealed class SceneObjectActorFactory : IActorFactory
{
    private readonly Node2D host;
    private readonly IActorTextureLoader textures;
    private readonly Vector2I resolution;
    private readonly IActorAppearanceTransitionService transitions;

    public SceneObjectActorFactory(Node2D host, IActorTextureLoader textures,
        Vector2I resolution,
        IActorAppearanceTransitionService? transitions = null)
    {
        this.host = host;
        this.textures = textures;
        this.resolution = resolution;
        this.transitions = transitions ?? new GodotActorAppearanceTransitionService();
    }

    public string FactoryId => ActorFactoryIds.SceneObject;

    public string GetInitialAppearance(ActorDefinition definition) =>
        definition.GetFactoryData<TextureActorDefinitionData>().DefaultAppearance;

    public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance) =>
        new[] { ResourcePreloadEntry.Create<Texture2D>(
            definition.GetFactoryData<TextureActorDefinitionData>().GetAppearanceResourceKey(
                definition.DefinitionId, appearance),
            definition.GetFactoryData<TextureActorDefinitionData>().GetAppearanceOverride(appearance)) };

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = new Sprite2D { Name = definition.Id, Visible = false };
        host.AddChild(view);
        try
        {
            var actor = new SceneObjectActor(definition, view, textures, resolution, transitions);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
        catch { view.QueueFree(); throw; }
    }
}
