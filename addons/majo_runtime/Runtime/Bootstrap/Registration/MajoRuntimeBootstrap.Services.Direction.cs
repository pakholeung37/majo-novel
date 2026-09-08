#nullable enable

using System.Linq;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Timing;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterDirectionServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<IEasingRegistry>(_ =>
                easingRegistry)
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
                    new DirectionTextureLoader(resolver.Get<IActorTextureLoader>()),
                    root => CreateDirectionActorManager(
                        root,
                        resolver.Get<IOperationRunner>(),
                        resolver.Get<IActorTextureLoader>(),
                        resolver.Get<IFrameClock>(),
                        resolver.Get<ILocaleManager>(),
                        resolver.Get<IEasingRegistry>()),
                    resolver.Get<ITransitionDriverRegistry>(),
                    resolver.Get<IEasingRegistry>()),

                typeof(GodotDirectionRenderHost),
                typeof(IResourceManager),
                typeof(IOperationRunner),
                typeof(IActorTextureLoader),
                typeof(IFrameClock),
                typeof(ILocaleManager),
                typeof(ITransitionDriverRegistry),
                typeof(IEasingRegistry))
            .AddSingleton<ICameraModifierManager>(
                resolver =>
                {
                    var host = resolver.Get<GodotDirectionRenderHost>();
                    var manager = new CameraModifierManager(
                        (viewId, delta) => host.ApplyViewModifierDelta(viewId, delta),
                        viewId => host.ClearViewModifierDelta(viewId),
                        resolver.Get<IOperationRunner>(),
                        resolver.Get<IDirectionAccessibility>(),
                        resolver.Get<IFrameClock>(),
                        registry: cameraModifiers);
                    host.ReleaseViewEffectsAsync = view => manager.ClearViewAsync(view);
                    return manager;
                },
                typeof(GodotDirectionRenderHost),
                typeof(IOperationRunner),
                typeof(IDirectionAccessibility),
                typeof(IFrameClock))
            .AddSingleton<IActorModifierManager>(resolver =>
                {
                    var host = resolver.Get<GodotDirectionRenderHost>();
                    var manager = new ActorModifierManager(host.FindActorModifierTarget,
                        resolver.Get<IOperationRunner>(), resolver.Get<IDirectionAccessibility>(), resolver.Get<IFrameClock>(), actorModifiers);
                    host.ReleaseActorEffectsAsync = target => manager.ClearTargetAsync(target);
                    return manager;
                }, typeof(GodotDirectionRenderHost), typeof(IOperationRunner), typeof(IDirectionAccessibility), typeof(IFrameClock))
            .AddSingleton<ITransientCueHandlerRegistry>(_ => transientCueHandlers)
            .AddSingleton<ITransientDirectionCueExecutor>(
                resolver => new TransientDirectionCueExecutor(
                    resolver.Get<ITransientCueHandlerRegistry>(),
                    resolver.Get<ICameraModifierManager>(),
                    resolver,
                    resolver.Get<IRuntimeActivityRegistry>()),
                typeof(ITransientCueHandlerRegistry),
                typeof(IActorModifierManager),
                typeof(ICameraModifierManager),
                typeof(IRuntimeActivityRegistry))
            .AddSingleton<IDirectionTransactionCoordinator>(
                resolver =>
                {
                    var host = resolver.Get<GodotDirectionRenderHost>();
                    var drivers = resolver.Get<ITransitionDriverRegistry>();
                    var easings = resolver.Get<IEasingRegistry>();
                    var backend = new CompositeDirectionPreparationBackend(
                        resolver.Get<GodotDirectionStageBackend>(),
                        new GodotDirectionViewBackend(host, drivers, easings),
                        new GodotDirectionScreenBackend(host, drivers, easings),
                        new GodotDirectionEffectBackend(host, easings),
                        plan =>
                        {
                            foreach (var intent in plan.Transitions)
                            {
                                easings.GetRequired(intent.Spec.Easing);
                            }
                        });
                    return new DirectionTransactionCoordinator(
                        new DirectionPlanner(),
                        backend,
                        activities: resolver.Get<IRuntimeActivityRegistry>(),
                        cues: resolver.Get<ITransientDirectionCueExecutor>(),
                        resetRendering: async token =>
                        {
                            await resolver.Get<GodotDirectionStageBackend>().ResetAsync(token);
                            // Scene teardown may already have freed the render surfaces.
                            if (GodotObject.IsInstanceValid(host.StageViewport))
                                await host.ResetAsync(token);
                        },
                        clearSceneRendering: async token =>
                        {
                            var failures = new System.Collections.Generic.List<System.Exception>();
                            foreach (var cleanup in new System.Func<System.Threading.Tasks.Task>[]
                            {
                                () => resolver.Get<ICameraModifierManager>().ResetAsync(token),
                                () => resolver.Get<IActorModifierManager>().ResetAsync(token),
                                () => resolver.Get<GodotDirectionStageBackend>().ResetAsync(token),
                                () => host.ResetSceneAsync(token),
                            })
                                try { await cleanup(); }
                                catch (System.Exception error) { failures.Add(error); }
                            if (failures.Count > 0) throw new System.AggregateException("Scene rendering cleanup failed.", failures);
                        });
                },
                typeof(GodotDirectionRenderHost),
                typeof(GodotDirectionStageBackend),
                typeof(ITransitionDriverRegistry),
                typeof(IEasingRegistry),
                typeof(IResourceManager),
                typeof(ICameraModifierManager),
                typeof(IActorModifierManager),
                typeof(IRuntimeActivityRegistry),
                typeof(ITransientDirectionCueExecutor))
            .AddSingleton<IDirectionBeatRuntime>(
                resolver => new DirectionBeatRuntime(
                    resolver.Get<IDirectionTransactionCoordinator>(),
                    resolver.Get<ITransientDirectionCueExecutor>()),
                typeof(IDirectionTransactionCoordinator),
                typeof(ITransientDirectionCueExecutor))
            .AddSingleton<IDirectionScopeResolver>(_ =>
                new DirectionScopeResolver(Configuration!.Actors!.CreateDirectionMarks()))
            .AddSingleton<IDirectionCommandPolicy>(
                resolver => new DirectionCommandPolicy(
                    resolver.Get<IDirectionBeatRuntime>(),
                    DirectionAnimationDefaults.FromConfiguration(Configuration?.Animation),
                    resolver.Get<ITransitionDriverRegistry>(),
                    resolver.Get<IEasingRegistry>()),
                typeof(IDirectionBeatRuntime),
                typeof(ITransitionDriverRegistry),
                typeof(IEasingRegistry))
            .AddSingleton<ISceneBoundaryCoordinator>(resolver =>
                {
                    var coordinator = new SceneBoundaryCoordinator(resolver.Get<IDirectionTransactionCoordinator>(),
                        resolver.Get<IDirectionBeatRuntime>(), resolver.Get<IAudioManager>());
                    if (resolver.Get<IYarnDialogueBackend>() is YarnDialogueBackend yarn)
                        yarn.BeforeNodeEntryAsync = coordinator.EnterNodeAsync;
                    return coordinator;
                }, typeof(IDirectionTransactionCoordinator), typeof(IDirectionBeatRuntime), typeof(IAudioManager), typeof(IYarnDialogueBackend))
            .AddSingleton<DirectionBeatNodeBoundaryService>(
                resolver => new DirectionBeatNodeBoundaryService(
                    resolver.Get<IYarnDialogueBackend>(),
                    resolver.Get<IDirectionBeatRuntime>()),
                typeof(IYarnDialogueBackend),
                typeof(ISceneBoundaryCoordinator),
                typeof(IDirectionBeatRuntime));
    }

    private ActorManager CreateDirectionActorManager(
        Node2D root,
        IOperationRunner scheduler,
        IActorTextureLoader textureLoader,
        IFrameClock frameClock,
        ILocaleManager locales,
        IEasingRegistry easingRegistry)
    {
        if (textureLoader is ResourceActorTextureLoader resourceLoader)
        {
            var scope = resourceLoader.CreateRetainedScope();
            root.TreeExiting += scope.Dispose;
            textureLoader = scope;
        }
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
                new SceneObjectActorFactory(root, scheduler, textureLoader, Configuration.Resources!,
                    actorConfiguration.ReferenceResolution, easingRegistry, transitionDrivers),
                new CharacterActorFactory(
                    root,
                    scheduler,
                    textureLoader,
                    new CharacterPositionMap(actorConfiguration),
                    easingRegistry, transitionDrivers),
                new LayeredCharacterActorFactory(
                    root,
                    scheduler,
                    textureLoader,
                    frameClock,
                    new CharacterPositionMap(actorConfiguration),
                    Configuration.Resources!.RootPath,
                    Configuration.Resources.CharactersPath,
                    easingRegistry, transitionDrivers),
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
