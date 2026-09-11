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

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = new Control
        {
            Name = definition.Id,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        host.AddChild(container);
        container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        try
        {
            var actor = new BackgroundActor(definition, container, textureLoader);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
        catch
        {
            container.QueueFree();
            throw;
        }
    }
}
