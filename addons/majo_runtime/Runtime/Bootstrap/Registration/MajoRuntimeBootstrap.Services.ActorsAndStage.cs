#nullable enable

using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Plugins;
using Majo.Runtime.Stage;
using Majo.Runtime.Stage.Godot;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterActorAndStageServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<GodotActorHost>(_ => new GodotActorHost(this, Configuration!.Actors!))
            .AddSingleton<IActorManager>(
                resolver => CreateActorManager(
                    resolver.Get<GodotActorHost>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IActorTextureLoader>(),
                    resolver.Get<IFrameClock>(),
                    resolver.Get<ILocaleManager>()),
                typeof(GodotActorHost),
                typeof(IOperationScheduler),
                typeof(IActorTextureLoader),
                typeof(IFrameClock),
                typeof(ILocaleManager))
            .AddSingleton<IPluginManager>(_ => pluginManager)
            .AddSingleton<IStageBackend>(
                resolver => new GodotStageBackend(this, resolver.Get<GodotActorHost>()),
                typeof(GodotActorHost))
            .AddSingleton<IStageAccessibility>(
                _ => new StageAccessibility(Configuration!.Stage!.ReduceMotion))
            .AddSingleton<ICameraManager>(
                resolver => new CameraManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IStageAccessibility>()),
                typeof(IStageBackend),
                typeof(IOperationScheduler),
                typeof(IStageAccessibility))
            .AddSingleton<IStageTransitionManager>(
                resolver =>
                {
                    var registry = new StageTransitionProviderRegistry();
                    ConfigureStageTransitionProviders?.Invoke(registry);
                    return new StageTransitionManager(
                        resolver.Get<IStageBackend>(),
                        resolver.Get<IOperationScheduler>(),
                        registry.Providers);
                },
                typeof(IStageBackend),
                typeof(IOperationScheduler))
            .AddSingleton<IStageCurtainManager>(
                resolver => new StageCurtainManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationScheduler>()),
                typeof(IStageBackend),
                typeof(IOperationScheduler))
            .AddSingleton<IScreenEffectManager>(
                resolver => new ScreenEffectManager(
                    resolver.Get<IStageBackend>(),
                    resolver.Get<IOperationScheduler>(),
                    resolver.Get<IStageAccessibility>()),
                typeof(IStageBackend),
                typeof(IOperationScheduler),
                typeof(IStageAccessibility));
    }

    private ActorManager CreateActorManager(
        GodotActorHost host,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader,
        IFrameClock frameClock,
        ILocaleManager locales)
    {
        var actorConfiguration = Configuration!.Actors!;
        var manager = new ActorManager(
            ActorConfigurationCompiler.Compile(actorConfiguration, Configuration.Resources!),
            new IActorFactory[]
            {
                new CharacterActorFactory(
                    host.CharacterLayer,
                    scheduler,
                    textureLoader,
                    new CharacterPositionMap(actorConfiguration)),
                new LayeredCharacterActorFactory(
                    host.CharacterLayer,
                    scheduler,
                    textureLoader,
                    frameClock,
                    new CharacterPositionMap(actorConfiguration),
                    Configuration.Resources!.RootPath,
                    Configuration.Resources.CharactersPath),
                new BackgroundActorFactory(
                    host.BackgroundLayer,
                    scheduler,
                    textureLoader),
            },
            locales,
            Logger,
            new ActorCreationConventions(
                Configuration.Resources!.CharactersPath,
                Configuration.Resources.BackgroundsPath));
        ConfigurePluginActorFactories(manager);
        ConfigureActorFactories?.Invoke(manager);
        return manager;
    }

    private void ConfigurePluginActorFactories(ActorManager manager)
    {
        foreach (var configure in pluginRegistration.ActorFactoryConfigurations)
            configure(manager);
    }
}
