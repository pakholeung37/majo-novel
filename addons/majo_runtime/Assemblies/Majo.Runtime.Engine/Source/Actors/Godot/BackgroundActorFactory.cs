using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class BackgroundActorFactory : IActorFactory
{
    private readonly Control host;
    private readonly IActorTextureLoader textureLoader;

    public BackgroundActorFactory(
        Control host,
        IActorTextureLoader textureLoader)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
    }

    public string FactoryId => ActorFactoryIds.Background;

    public string GetInitialAppearance(ActorDefinition definition) =>
        definition.GetFactoryData<TextureActorDefinitionData>().DefaultAppearance;

    public System.Collections.Generic.IReadOnlyList<Majo.Runtime.Resources.ResourcePreloadEntry> GetPreloadResources(
        ActorDefinition definition, string appearance)
    {
        var data = definition.GetFactoryData<TextureActorDefinitionData>();
        return new[]
        {
            Majo.Runtime.Resources.ResourcePreloadEntry.Create<Texture2D>(
                data.GetAppearanceResourceKey(definition.DefinitionId, appearance),
                data.GetAppearanceOverride(appearance)),
        };
    }

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public async Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = new Node2D
        {
            Name = definition.Id,
            Visible = false,
        };
        host.AddChild(container);
        try
        {
            var actor = new BackgroundActor(definition, container, textureLoader, initializeAppearance: false);
            try
            {
                await using var prepared = await actor.PrepareBackgroundAppearanceAsync(GetInitialAppearance(definition), cancellationToken);
                prepared.Commit();
                prepared.ConfirmCommit();
                cancellationToken.ThrowIfCancellationRequested();
                actor.SetVisible(mode == ActorCreationMode.Active);
                return actor;
            }
            catch { actor.Destroy(); throw; }
        }
        catch
        {
            container.QueueFree();
            throw;
        }
    }
}
