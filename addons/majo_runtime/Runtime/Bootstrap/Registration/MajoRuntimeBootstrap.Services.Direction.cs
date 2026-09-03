#nullable enable

using System.Linq;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Stage;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterDirectionServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<ITransitionDriverRegistry>(_ =>
                transitionDrivers)
            .AddSingleton<GodotDirectionRenderHost>(
                _ => new GodotDirectionRenderHost(
                    this,
                    Configuration!.Actors!.ReferenceResolution,
                    Configuration!.Stage!.MaxDirectionStages))
            .AddSingleton<GodotDirectionStageBackend>(
                resolver => new GodotDirectionStageBackend(
                    resolver.Get<GodotDirectionRenderHost>(),
                    new DirectionTextureLoader(resolver.Get<IResourceManager>()),
                    root => CreateDirectionActorManager(
                        root,
                        resolver.Get<IOperationScheduler>(),
                        resolver.Get<IActorTextureLoader>(),
                        resolver.Get<IFrameClock>(),
                        resolver.Get<ILocaleManager>()),
                    resolver.Get<ITransitionDriverRegistry>()),
                typeof(GodotDirectionRenderHost),
                typeof(IResourceManager),
                typeof(IOperationScheduler),
                typeof(IActorTextureLoader),
                typeof(IFrameClock),
                typeof(ILocaleManager),
                typeof(ITransitionDriverRegistry))
            .AddSingleton<IDirectionTransactionCoordinator>(
                resolver =>
                {
                    var host = resolver.Get<GodotDirectionRenderHost>();
                    var drivers = resolver.Get<ITransitionDriverRegistry>();
                    var textures = new DirectionTextureLoader(
                        resolver.Get<IResourceManager>());
                    var backend = new CompositeDirectionPreparationBackend(
                        resolver.Get<GodotDirectionStageBackend>(),
                        new GodotDirectionViewBackend(host, drivers),
                        new GodotDirectionScreenBackend(host),
                        new GodotDirectionEffectBackend(host));
                    return new DirectionTransactionCoordinator(
                        new DirectionPlanner(),
                        backend);
                },
                typeof(GodotDirectionRenderHost),
                typeof(GodotDirectionStageBackend),
                typeof(ITransitionDriverRegistry),
                typeof(IResourceManager))
            .AddSingleton<ICameraModifierManager>(
                resolver =>
                {
                    var host = resolver.Get<GodotDirectionRenderHost>();
                    var manager = new CameraModifierManager(
                        (viewId, delta) => host.ApplyViewModifierDelta(viewId, delta),
                        viewId => host.ClearViewModifierDelta(viewId),
                        resolver.Get<IOperationScheduler>(),
                        resolver.Get<IStageAccessibility>(),
                        resolver.Get<IFrameClock>(),
                        registry: cameraModifiers);
                    return manager;
                },
                typeof(GodotDirectionRenderHost),
                typeof(IOperationScheduler),
                typeof(IStageAccessibility),
                typeof(IFrameClock))
            .AddSingleton<ITransientCueHandlerRegistry>(_ => transientCueHandlers)
            .AddSingleton<IDirectionBeatRuntime>(
                resolver => new DirectionBeatRuntime(
                    resolver.Get<IDirectionTransactionCoordinator>(),
                    new TransientDirectionCueExecutor(
                        resolver.Get<ITransientCueHandlerRegistry>(),
                        resolver.Get<ICameraModifierManager>(),
                        resolver.Get<IScreenEffectManager>(),
                        resolver.Get<IStageCurtainManager>(),
                        resolver)),
                typeof(IDirectionTransactionCoordinator),
                typeof(IScreenEffectManager),
                typeof(IStageCurtainManager),
                typeof(ICameraModifierManager),
                typeof(ITransientCueHandlerRegistry))
            .AddSingleton<IDirectionScopeResolver>(_ => new DirectionScopeResolver())
            .AddSingleton<DirectionBeatNodeBoundaryService>(
                resolver => new DirectionBeatNodeBoundaryService(
                    resolver.Get<IYarnDialogueBackend>(),
                    resolver.Get<IDirectionBeatRuntime>()),
                typeof(IYarnDialogueBackend),
                typeof(IDirectionBeatRuntime));
    }

    private ActorManager CreateDirectionActorManager(
        Node2D root,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader,
        IFrameClock frameClock,
        ILocaleManager locales)
    {
        var actorConfiguration = Configuration!.Actors!;
        var manager = new ActorManager(
            ActorConfigurationCompiler.Compile(
                actorConfiguration,
                Configuration.Resources!)
                .Where(definition =>
                    definition.FactoryId != ActorFactoryIds.Background)
                .ToArray(),
            new IActorFactory[]
            {
                new CharacterActorFactory(
                    root,
                    scheduler,
                    textureLoader,
                    new CharacterPositionMap(actorConfiguration)),
                new LayeredCharacterActorFactory(
                    root,
                    scheduler,
                    textureLoader,
                    frameClock,
                    new CharacterPositionMap(actorConfiguration),
                    Configuration.Resources!.RootPath,
                    Configuration.Resources.CharactersPath),
            },
            locales,
            Logger,
            new ActorCreationConventions(
                Configuration.Resources!.CharactersPath,
                Configuration.Resources.BackgroundsPath));
        ConfigurePluginActorFactories(manager);
        return manager;
    }
}
