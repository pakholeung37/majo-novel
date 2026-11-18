#nullable enable

using System.Collections.Generic;
using Majo.Runtime.Configuration;

namespace Majo.Runtime.Actors.Godot;

public static class ActorConfigurationCompiler
{
    public static IReadOnlyList<ActorDefinition> Compile(
        ActorConfiguration configuration,
        ActorResourceSettings resources,
        ActorFactoryRegistry registry)
    {
        var definitions = new List<ActorDefinition>(configuration.Actors.Count);
        foreach (var configured in configuration.Actors)
        {
            if (configured is null)
                throw new ActorDefinitionException("<null>", "Definition is required.");
            var registration = registry.GetRequired(configured.FactoryId);
            definitions.Add(registration.CompileDefinition is { } compile
                ? compile(configured, resources)
                : new ActorDefinition(configured.Id, configured.FactoryId, configured.FactoryConfiguration));
        }
        return definitions;
    }

    public static ActorDefinition CompileTextureDefinition(
        ActorDefinitionConfiguration configured,
        ActorResourceSettings resources) =>
        CompileTextureDefinition(configured, resources.CharactersPath);

    public static ActorDefinition CompileCharacterTextureDefinition(
        ActorDefinitionConfiguration configured,
        ActorResourceSettings resources) =>
        CompileTextureDefinition(configured, resources.CharactersPath);

    public static ActorDefinition CompileBackgroundTextureDefinition(
        ActorDefinitionConfiguration configured,
        ActorResourceSettings resources) =>
        CompileTextureDefinition(configured, resources.BackgroundsPath);

    public static ActorDefinition CompileSceneObjectTextureDefinition(
        ActorDefinitionConfiguration configured,
        ActorResourceSettings resources) =>
        CompileTextureDefinition(configured, resources.ObjectsPath);

    private static ActorDefinition CompileTextureDefinition(
        ActorDefinitionConfiguration configured,
        string defaultPrefix)
    {
        var texture = configured.FactoryConfiguration as TextureActorConfiguration ??
            throw new ActorDefinitionException(
                configured.Id,
                $"Factory '{configured.FactoryId}' requires {nameof(TextureActorConfiguration)}.");
        var appearances = new List<KeyValuePair<string, string>>(texture.Appearances.Count);
        foreach (var appearance in texture.Appearances)
        {
            if (appearance is null)
                throw new ActorDefinitionException(configured.Id, "Appearance definition is required.");
            appearances.Add(new(appearance.Id, appearance.TexturePath));
        }
        var prefix = !string.IsNullOrWhiteSpace(texture.ResourcePathPrefix)
            ? texture.ResourcePathPrefix
            : defaultPrefix;
        return new ActorDefinition(
            configured.Id,
            configured.FactoryId,
            new TextureActorDefinitionData(
                configured.Id,
                prefix,
                texture.DefaultAppearance,
                appearances,
                texture.InitialPosition));
    }
}
