#nullable enable

using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Plugins;

namespace Majo.Demos.Manosaba.Actors.Layered;

public sealed class LayeredCharacterPlugin : IMajoPlugin
{
    public string Id => LayeredActorIds.Factory;

    public void ConfigureServices(RuntimeServiceRegistry services) =>
        services.AddSingleton<LayeredCharacterManifestCache>(_ => new LayeredCharacterManifestCache());

    public void Configure(PluginRegistrationContext context)
    {
        context.ActorFactories.Register(
            LayeredActorIds.Factory,
            ActorFactoryDomains.Runtime | ActorFactoryDomains.Direction,
            factoryContext => new LayeredCharacterActorFactory(factoryContext),
            (configuration, _) =>
            {
                var layered = configuration.FactoryConfiguration as LayeredCharacterConfiguration ??
                    throw new ActorDefinitionException(configuration.Id, "Layered configuration is required.");
                return new ActorDefinition(configuration.Id, configuration.FactoryId,
                    new LayeredCharacterDefinitionData(
                        string.IsNullOrWhiteSpace(layered.AssetId) ? configuration.Id : layered.AssetId,
                        layered.ManifestPath, layered.InitialPosition));
            },
            typeof(LayeredCharacterManifestCache));
    }
}
