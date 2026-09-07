#nullable enable

using Godot;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Plugins;
using Majo.Runtime.Timing;

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
                    resolver.Get<IOperationRunner>(),
                    resolver.Get<IActorTextureLoader>(),
                    resolver.Get<IFrameClock>(),
                    resolver.Get<ILocaleManager>(),
                    resolver.Get<IEasingRegistry>()),
                typeof(GodotActorHost),
                typeof(IOperationRunner),
                typeof(IActorTextureLoader),
                typeof(IFrameClock),
                typeof(ILocaleManager),
                typeof(IEasingRegistry))

            .AddSingleton<IPluginManager>(_ => pluginManager)
            .AddSingleton<IDirectionAccessibility>(
                _ => new DirectionAccessibility(Configuration!.Stage!.ReduceMotion));
    }

    private ActorManager CreateActorManager(
        GodotActorHost host,
        IOperationRunner scheduler,
        IActorTextureLoader textureLoader,
        IFrameClock frameClock,
        ILocaleManager locales,
        IEasingRegistry easingRegistry)
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
                    new CharacterPositionMap(actorConfiguration),
                    easingRegistry, transitionDrivers),
                new LayeredCharacterActorFactory(
                    host.CharacterLayer,
                    scheduler,
                    textureLoader,
                    frameClock,
                    new CharacterPositionMap(actorConfiguration),
                    Configuration.Resources!.RootPath,
                    Configuration.Resources.CharactersPath,
                    easingRegistry, transitionDrivers),
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
