#nullable enable

using System.Linq;
using Godot;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Audio;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Timing;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterDirectionServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<DirectionActorDefinitionEnvironment>(resolver =>
                new DirectionActorDefinitionEnvironment(this, roots => CreateDirectionActorManager(roots, resolver), Configuration!.Actors!.ReferenceResolution),
                ActorFactoryServiceDependencies(ActorFactoryDomains.Direction,
                    typeof(IResourceManager), typeof(IActorTextureLoader), typeof(IFrameClock), typeof(ILocaleManager)))
            .AddSingleton<CameraEnvironmentPresetCatalog>(_ =>
                new CameraEnvironmentPresetCatalog(Configuration!.CameraEnvironment))
            .AddSingleton<IEasingRegistry>(_ =>
                easingRegistry)
            .AddSingleton<ITransitionDriverRegistry>(_ =>
                transitionDrivers)
            .AddSingleton<GodotDirectionRenderHost>(
                resolver => new GodotDirectionRenderHost(
                    this,
                    Configuration!.Actors!.ReferenceResolution,
                    Configuration!.Stage!.MaxDirectionStages,
                    resolver.Get<IFrameClock>(),
                    Logger),
                typeof(IFrameClock))
            .AddSingleton<GodotDirectionStageBackend>(
                resolver => new GodotDirectionStageBackend(
                    resolver.Get<GodotDirectionRenderHost>(),
                    root => CreateDirectionActorManager(root, resolver),
                    resolver.Get<ITransitionDriverRegistry>(),
                    resolver.Get<IEasingRegistry>()),

                ActorFactoryServiceDependencies(
                    ActorFactoryDomains.Direction,
                    typeof(DirectionActorDefinitionEnvironment),
                    typeof(GodotDirectionRenderHost),
                    typeof(IResourceManager),
                    typeof(IOperationRunner),
                    typeof(IActorTextureLoader),
                    typeof(IFrameClock),
                    typeof(ILocaleManager),
                    typeof(ITransitionDriverRegistry),
                    typeof(IEasingRegistry)))
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
                            foreach (var stage in plan.TargetState.Stages.Values)
                                if (stage.Backdrop is { } backdrop)
                                {
                                    var resolved = resolver.Get<DirectionActorDefinitionEnvironment>().Resolver.ResolveBackdropAppearance(
                                        backdrop.DefinitionId, backdrop.Appearance, requiredFactoryId: backdrop.FactoryId);
                                    if (resolved.Appearance != backdrop.Appearance)
                                        throw new DirectionException("Stored background appearance must be a complete value.");
                                }
                            foreach (var intent in plan.Transitions)
                            {
                                easings.GetRequired(intent.Spec.Easing);
                            }
                        });
                    return new DirectionTransactionCoordinator(
                        new DirectionPlanner(resolver.Get<DirectionActorDefinitionEnvironment>().Resolver),
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
                        },
                        sampleState: host.SampleDynamicState);
                },
                typeof(GodotDirectionRenderHost),
                typeof(GodotDirectionStageBackend),
                typeof(DirectionActorDefinitionEnvironment),
                typeof(ITransitionDriverRegistry),
                typeof(IEasingRegistry),
                typeof(IResourceManager),
                typeof(ICameraModifierManager),
                typeof(IActorModifierManager),
                typeof(IRuntimeActivityRegistry),
                typeof(ITransientDirectionCueExecutor))
            .AddSingleton<DirectionBackgroundLocaleRefresh>(resolver => new DirectionBackgroundLocaleRefresh(
                resolver.Get<ILocaleManager>(), resolver.Get<IDirectionTransactionCoordinator>(),
                resolver.Get<GodotDirectionStageBackend>(), Logger!),
                typeof(ILocaleManager), typeof(IDirectionTransactionCoordinator), typeof(GodotDirectionStageBackend))
            .AddSingleton<IDirectionBeatRuntime>(
                resolver => new DirectionBeatRuntime(
                    resolver.Get<IDirectionTransactionCoordinator>(),
                    resolver.Get<ITransientDirectionCueExecutor>()),
                typeof(IDirectionTransactionCoordinator),
                typeof(ITransientDirectionCueExecutor))
            .AddSingleton<IDirectionScopeResolver>(resolver =>
                new DirectionScopeResolver(Configuration!.Actors!.CreateDirectionMarks(),
                    resolver.Get<CameraEnvironmentPresetCatalog>()),
                typeof(CameraEnvironmentPresetCatalog))
            .AddSingleton<IDirectionCommandPolicy>(
                resolver => new DirectionCommandPolicy(
                    resolver.Get<IDirectionBeatRuntime>(),
                    DirectionAnimationDefaults.FromConfiguration(Configuration?.Animation),
                    resolver.Get<ITransitionDriverRegistry>(),
                    resolver.Get<IEasingRegistry>()),
                typeof(IDirectionBeatRuntime),
                typeof(ITransitionDriverRegistry),
                typeof(IEasingRegistry))
            .AddSingleton<ScenePreparationRegistry>(resolver =>
                {
                    var preparation = new ScenePreparationRegistry(resolver.Get<IResourceManager>());
                    ConfigureScenePreparation?.Invoke(preparation);
                    return preparation;
                }, typeof(IResourceManager))
            .AddSingleton<ISceneBoundaryCoordinator>(resolver =>
                {
                    var coordinator = new SceneBoundaryCoordinator(resolver.Get<IDirectionTransactionCoordinator>(),
                        resolver.Get<IDirectionBeatRuntime>(), resolver.Get<IAudioManager>(), resolver.Get<ScenePreparationRegistry>());
                    if (resolver.Get<IYarnDialogueBackend>() is YarnDialogueBackend yarn)
                        yarn.BeforeNodeEntryAsync = coordinator.EnterNodeAsync;
                    return coordinator;
                }, typeof(IDirectionTransactionCoordinator), typeof(IDirectionBeatRuntime), typeof(IAudioManager), typeof(IYarnDialogueBackend), typeof(ScenePreparationRegistry))
            .AddSingleton<DirectionBeatNodeBoundaryService>(
                resolver => new DirectionBeatNodeBoundaryService(
                    resolver.Get<IYarnDialogueBackend>(),
                    resolver.Get<IDirectionBeatRuntime>()),
                typeof(IYarnDialogueBackend),
                typeof(ISceneBoundaryCoordinator),
                typeof(IDirectionBeatRuntime));
    }

    private ActorManager CreateDirectionActorManager(ActorMountRoots mounts, IServiceResolver resolver)
    {
        IActorTextureLoader textureLoader = resolver.Get<IActorTextureLoader>();
        if (textureLoader is ResourceActorTextureLoader resourceLoader)
        {
            var scope = resourceLoader.CreateRetainedScope();
            mounts.CharacterRoot.TreeExiting += scope.Dispose;
            textureLoader = scope;
        }
        var context = new ActorFactoryContext(
            ActorFactoryDomains.Direction,
            mounts,
            textureLoader,
            resolver.Get<IResourceManager>(),
            resolver.Get<IFrameClock>(),
            actorCatalog!,
            resolver,
            Logger!);
        var manager = ActorManagerBuilder.Create(
            context,
            resolver.Get<ILocaleManager>(),
            new ActorCreationConventions(
                context.ResourceSettings.CharactersPath,
                context.ResourceSettings.BackgroundsPath));
        manager.RefreshBackgroundsOnLocaleChange = false;
        return manager;
    }
}
