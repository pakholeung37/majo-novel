#nullable enable

using System.Linq;
using Godot;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Plugins;
using Majo.Runtime.Resources;
using Majo.Runtime.Timing;

namespace Majo.Runtime;

public partial class MajoRuntimeBootstrap : Node
{
    private void RegisterActorAndStageServices(RuntimeServiceRegistry services)
    {
        services
            .AddSingleton<GodotActorHost>(_ => new GodotActorHost(this, Configuration!.Actors!))
            .AddSingleton<IActorManager>(
                resolver => CreateActorManager(resolver, resolver.Get<GodotActorHost>()),
                ActorFactoryServiceDependencies(
                    ActorFactoryDomains.Runtime,
                    typeof(GodotActorHost),
                    typeof(IActorTextureLoader),
                    typeof(IFrameClock),
                    typeof(ILocaleManager),
                    typeof(IResourceManager)))

            .AddSingleton<IPluginManager>(_ => pluginManager)
            .AddSingleton<IDirectionAccessibility>(
                _ => new DirectionAccessibility(Configuration!.Stage!.ReduceMotion));
    }

    private ActorManager CreateActorManager(IServiceResolver resolver, GodotActorHost host)
    {
        var context = CreateActorFactoryContext(
            ActorFactoryDomains.Runtime, host.CharacterLayer, host.BackgroundLayer, resolver);
        return ActorManagerBuilder.Create(
            context,
            resolver.Get<ILocaleManager>(),
            new ActorCreationConventions(
                context.ResourceSettings.CharactersPath,
                context.ResourceSettings.BackgroundsPath), host);
    }

    private ActorFactoryContext CreateActorFactoryContext(
        ActorFactoryDomains domain,
        Node2D characterRoot,
        Control backgroundRoot,
        IServiceResolver resolver)
    {
        return new ActorFactoryContext(
            domain,
            new ActorMountRoots(characterRoot, backgroundRoot),
            resolver.Get<IActorTextureLoader>(),
            resolver.Get<Majo.Runtime.Resources.IResourceManager>(),
            resolver.Get<IFrameClock>(),
            actorCatalog!,
            resolver,
            Logger!);
    }

    private void RegisterBuiltInActorFactories()
    {
        actorFactories
            .Register(
                ActorFactoryIds.SceneObject,
                ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
                context => new SceneObjectActorFactory(
                    context.CharacterRoot,
                    context.TextureLoader,
                    context.ResourceSettings,
                    context.ActorSettings.ReferenceResolution,
                    context.AppearanceTransitions),
                ActorConfigurationCompiler.CompileSceneObjectTextureDefinition)
            .Register(
                ActorFactoryIds.Character,
                ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
                context => new CharacterActorFactory(
                    context.CharacterRoot,
                    context.TextureLoader,
                    context.CharacterPositions,
                    context.AppearanceTransitions),
                ActorConfigurationCompiler.CompileCharacterTextureDefinition)
            .Register(
                ActorFactoryIds.Background,
                ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
                context => new BackgroundActorFactory(
                    context.BackgroundRoot,
                    context.TextureLoader),
                ActorConfigurationCompiler.CompileBackgroundTextureDefinition);
    }

    private System.Type[] ActorFactoryServiceDependencies(
        ActorFactoryDomains domain,
        params System.Type[] builtIns) => builtIns
        .Concat(actorFactories.Registrations
            .Where(entry => (entry.Domains & domain) != 0)
            .SelectMany(entry => entry.ServiceDependencies))
        .Distinct()
        .ToArray();
}
