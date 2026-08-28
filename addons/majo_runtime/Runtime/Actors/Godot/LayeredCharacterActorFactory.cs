#nullable enable

using System;
using Godot;
using Majo.Runtime.Actors.Layered;
using Majo.Runtime.Configuration;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class LayeredCharacterActorFactory : IConventionActorFactory
{
    private const string ManifestFileName = "layered_character.json";
    private readonly Node2D host;
    private readonly IOperationScheduler scheduler;
    private readonly IActorTextureLoader textureLoader;
    private readonly CharacterPositionMap positions;
    private readonly string charactersPath;

    public LayeredCharacterActorFactory(
        Node2D host,
        IOperationScheduler scheduler,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        string resourceRootPath,
        string charactersPath)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(charactersPath);
        this.charactersPath = ResolveCharactersPath(resourceRootPath, charactersPath);
    }

    public string FactoryId => ActorFactoryIds.LayeredCharacter;

    public bool TryResolveDefinition(
        ActorSpawnRequest request,
        out ActorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FallbackFactoryId is not ActorFactoryIds.Character and
            not ActorFactoryIds.LayeredCharacter)
        {
            definition = null!;
            return false;
        }

        var manifestPath = $"{charactersPath}/{request.ActorId}/{ManifestFileName}";
        if (!global::Godot.FileAccess.FileExists(manifestPath))
        {
            definition = null!;
            return false;
        }

        var manifest = LoadManifest(manifestPath);
        if (!string.Equals(manifest.Id, request.ActorId, StringComparison.Ordinal))
        {
            throw new ActorDefinitionException(
                request.ActorId,
                $"Layered character manifest ID is '{manifest.Id}'.");
        }

        definition = new ActorDefinition(
            request.ActorId,
            FactoryId,
            new LayeredCharacterConfiguration
            {
                ManifestPath = manifestPath,
                InitialPosition = request.InitialPosition ?? CharacterPositionPreset.Center,
            });
        return true;
    }

    public void ValidateDefinition(ActorDefinition definition)
    {
        var configuration = definition.GetFactoryData<LayeredCharacterConfiguration>();
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ManifestPath);
        if (!global::Godot.FileAccess.FileExists(configuration.ManifestPath))
        {
            throw new ActorDefinitionException(
                definition.Id,
                $"Layered character manifest does not exist: '{configuration.ManifestPath}'.");
        }

        var manifest = LoadManifest(configuration.ManifestPath);
        if (!string.Equals(manifest.Id, definition.Id, StringComparison.Ordinal))
        {
            throw new ActorDefinitionException(
                definition.Id,
                $"Layered character manifest ID is '{manifest.Id}'.");
        }
    }

    public IActor Create(ActorDefinition definition)
    {
        var configuration = definition.GetFactoryData<LayeredCharacterConfiguration>();
        var manifest = LoadManifest(configuration.ManifestPath);
        var view = new Sprite2D
        {
            Name = definition.Id,
            Centered = true,
        };
        host.AddChild(view);
        try
        {
            return new LayeredCharacterActor(
                definition,
                view,
                scheduler,
                textureLoader,
                positions,
                manifest,
                configuration.ManifestPath,
                configuration.InitialPosition);
        }
        catch
        {
            view.QueueFree();
            throw;
        }
    }

    private static LayeredCharacterManifest LoadManifest(string path)
    {
        using var file = global::Godot.FileAccess.Open(
            path,
            global::Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            throw new ActorException(
                $"Layered character manifest could not be opened: '{path}'.");
        }

        return LayeredCharacterManifest.Parse(file.GetAsText());
    }

    private static string ResolveCharactersPath(
        string resourceRootPath,
        string configuredCharactersPath)
    {
        if (configuredCharactersPath.StartsWith("res://", StringComparison.Ordinal))
        {
            return configuredCharactersPath.TrimEnd('/');
        }

        return $"{resourceRootPath.TrimEnd('/')}/{configuredCharactersPath.Trim('/')}";
    }
}
