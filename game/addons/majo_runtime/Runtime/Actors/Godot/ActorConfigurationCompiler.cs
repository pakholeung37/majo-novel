using System.Collections.Generic;
using Majo.Runtime.Configuration;

namespace Majo.Runtime.Actors.Godot;

public static class ActorConfigurationCompiler
{
    public static IReadOnlyList<ActorDefinition> Compile(ActorConfiguration configuration) =>
        Compile(configuration, new ResourceConfiguration());

    public static IReadOnlyList<ActorDefinition> Compile(
        ActorConfiguration configuration,
        ResourceConfiguration resources)
    {
        var definitions = new List<ActorDefinition>(configuration.Actors.Count);
        foreach (var configuredActor in configuration.Actors)
        {
            if (configuredActor is null)
            {
                throw new ActorDefinitionException("<null>", "Definition is required.");
            }

            if (configuredActor.FactoryConfiguration is not null)
            {
                definitions.Add(new ActorDefinition(
                    configuredActor.Id,
                    configuredActor.FactoryId,
                    configuredActor.FactoryConfiguration));
                continue;
            }

            if (configuredActor.FactoryId is not ActorFactoryIds.Character and
                not ActorFactoryIds.Background &&
                configuredActor.Appearances.Count == 0 &&
                string.IsNullOrWhiteSpace(configuredActor.ResourcePathPrefix))
            {
                definitions.Add(new ActorDefinition(
                    configuredActor.Id, configuredActor.FactoryId));
                continue;
            }

            var appearances = new List<KeyValuePair<string, string>>(
                configuredActor.Appearances.Count);
            foreach (var configuredAppearance in configuredActor.Appearances)
            {
                if (configuredAppearance is null)
                {
                    throw new ActorDefinitionException(
                        configuredActor.Id, "Appearance definition is required.");
                }

                appearances.Add(new KeyValuePair<string, string>(
                    configuredAppearance.Id,
                    configuredAppearance.TexturePath));
            }

            definitions.Add(new ActorDefinition(
                configuredActor.Id,
                configuredActor.FactoryId,
                ResolveResourcePathPrefix(configuredActor, resources),
                configuredActor.DefaultAppearance,
                appearances,
                configuredActor.InitialPosition));
        }

        return definitions;
    }

    private static string ResolveResourcePathPrefix(
        ActorDefinitionConfiguration actor,
        ResourceConfiguration resources)
    {
        if (!string.IsNullOrWhiteSpace(actor.ResourcePathPrefix))
        {
            return actor.ResourcePathPrefix;
        }

        return actor.FactoryId switch
        {
            ActorFactoryIds.Character => resources.CharactersPath,
            ActorFactoryIds.Background => resources.BackgroundsPath,
            _ => actor.FactoryId,
        };
    }
}
