#nullable enable

using System;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Actors.Godot;

public sealed class ActorFactoryContext
{
    private readonly Control? backgroundRoot;

    public ActorFactoryContext(
        ActorFactoryDomains domain,
        Node2D characterRoot,
        Control? backgroundRoot,
        IActorTextureLoader textureLoader,
        IResourceManager resources,
        IFrameClock frameClock,
        ActorFactoryCatalog catalog,
        IServiceResolver services,
        IRuntimeLogger logger)
    {
        if (domain is not ActorFactoryDomains.Runtime and not ActorFactoryDomains.Direction)
            throw new ArgumentOutOfRangeException(nameof(domain));
        Domain = domain;
        CharacterRoot = characterRoot ?? throw new ArgumentNullException(nameof(characterRoot));
        this.backgroundRoot = backgroundRoot;
        TextureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        FrameClock = frameClock ?? throw new ArgumentNullException(nameof(frameClock));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        CharacterPositions = new CharacterPositionMap(catalog.ActorSettings);
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        AppearanceTransitions = new GodotActorAppearanceTransitionService();
    }

    public ActorFactoryDomains Domain { get; }
    public Node2D CharacterRoot { get; }
    public Control BackgroundRoot => backgroundRoot ?? throw new ActorException(
        "The Direction actor factory environment does not provide a background root.");
    public IActorTextureLoader TextureLoader { get; }
    public IResourceManager Resources { get; }
    public IFrameClock FrameClock { get; }
    public CharacterPositionMap CharacterPositions { get; }
    internal ActorFactoryCatalog Catalog { get; }
    public ActorRenderSettings ActorSettings => Catalog.ActorSettings;
    public ActorResourceSettings ResourceSettings => Catalog.ResourceSettings;
    public IServiceResolver Services { get; }
    public IRuntimeLogger Logger { get; }
    public IActorAppearanceTransitionService AppearanceTransitions { get; }

}
