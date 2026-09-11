#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Majo.Runtime.Configuration;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Immutable settings shared by every actor environment in one runtime.</summary>
public sealed record ActorRenderSettings(
    Vector2I ReferenceResolution,
    Vector2 ScenePositionOffset,
    Vector2 LeftPosition,
    Vector2 CenterPosition,
    Vector2 RightPosition)
{
    public static ActorRenderSettings Capture(ActorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(configuration.ReferenceResolution, configuration.ScenePositionOffset,
            configuration.LeftPosition, configuration.CenterPosition, configuration.RightPosition);
    }
}

public sealed record ActorResourceSettings(
    string RootPath,
    string CharactersPath,
    string BackgroundsPath,
    string ObjectsPath)
{
    public static ActorResourceSettings Capture(ResourceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(configuration.RootPath, configuration.CharactersPath,
            configuration.BackgroundsPath, configuration.ObjectsPath);
    }
}

/// <summary>
/// Compiles definitions once after registration is sealed. Factories receive the
/// captured settings rather than the project's mutable Godot configuration.
/// </summary>
public sealed class ActorFactoryCatalog
{
    public ActorFactoryCatalog(ActorFactoryRegistry registry,
        ActorConfiguration actors, ResourceConfiguration resources)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!registry.IsSealed) throw new ActorException("Seal actor registrations before compiling definitions.");
        Registry = registry;
        ActorSettings = ActorRenderSettings.Capture(actors);
        ResourceSettings = ActorResourceSettings.Capture(resources);
        Definitions = Array.AsReadOnly(ActorConfigurationCompiler.Compile(actors, ResourceSettings, registry).ToArray());
    }

    internal ActorFactoryRegistry Registry { get; }
    public ActorRenderSettings ActorSettings { get; }
    public ActorResourceSettings ResourceSettings { get; }
    public IReadOnlyList<ActorDefinition> Definitions { get; }
}
