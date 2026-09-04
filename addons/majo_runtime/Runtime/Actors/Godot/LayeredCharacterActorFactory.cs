#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors.Layered;
using Majo.Runtime.Configuration;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class LayeredCharacterActorFactory : IConventionActorFactory
{
    private const string ManifestFileName = "layered_character.json";
    private readonly Node2D host;
    private readonly IOperationRunner runner;
    private readonly IActorTextureLoader textureLoader;
    private readonly IFrameClock? frameClock;
    private readonly CharacterPositionMap positions;
    private readonly string charactersPath;
    private readonly ShaderMaterial multiplyMaterial;
    private readonly Dictionary<string, Task<LayeredCharacterManifest>> manifestLoads =
        new(StringComparer.Ordinal);
    private readonly object manifestGate = new();

    public LayeredCharacterActorFactory(
        Node2D host,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        CharacterPositionMap positions,
        string resourceRootPath,
        string charactersPath)
        : this(
            host,
            runner,
            textureLoader,
            null!,
            positions,
            resourceRootPath,
            charactersPath)
    {
    }

    public LayeredCharacterActorFactory(
        Node2D host,
        IOperationRunner runner,
        IActorTextureLoader textureLoader,
        IFrameClock frameClock,
        CharacterPositionMap positions,
        string resourceRootPath,
        string charactersPath)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.textureLoader = textureLoader ?? throw new ArgumentNullException(nameof(textureLoader));
        this.frameClock = frameClock;
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(charactersPath);
        this.charactersPath = ResolveCharactersPath(resourceRootPath, charactersPath);
        multiplyMaterial = LayeredCharacterActor.CreateAlphaAwareMultiplyMaterial();
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

        var manifest = GetManifest(configuration.ManifestPath);
        ValidateManifestId(definition.Id, manifest);
    }

    public IActor Create(ActorDefinition definition)
    {
        var configuration = definition.GetFactoryData<LayeredCharacterConfiguration>();
        var manifest = GetManifest(configuration.ManifestPath);
        ValidateManifestId(definition.Id, manifest);
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
                runner,
                textureLoader,
                positions,
                manifest,
                configuration.ManifestPath,
                configuration.InitialPosition,
                multiplyMaterial);
        }
        catch
        {
            view.QueueFree();
            throw;
        }
    }

    public async Task<IActor> CreateAsync(
        ActorDefinition definition,
        CancellationToken cancellationToken)
    {
        var configuration = definition.GetFactoryData<LayeredCharacterConfiguration>();
        var manifest = await GetManifestAsync(
            configuration.ManifestPath,
            cancellationToken);
        ValidateManifestId(definition.Id, manifest);
        var manifestDirectory = GetDirectory(configuration.ManifestPath);
        var atlasNames = manifest.Layers
            .Select(layer => layer.Atlas)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var loading = atlasNames.ToDictionary(
            atlas => atlas,
            atlas => textureLoader.LoadAsync(
                $"{manifestDirectory}/{atlas}",
                cancellationToken),
            StringComparer.Ordinal);
        await Task.WhenAll(loading.Values);
        cancellationToken.ThrowIfCancellationRequested();
        var atlases = loading.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Result,
            StringComparer.Ordinal);

        var view = new Sprite2D
        {
            Name = definition.Id,
            Centered = true,
            Visible = false,
        };
        host.AddChild(view);
        LayeredCharacterActor? actor = null;
        try
        {
            actor = new LayeredCharacterActor(
                definition,
                view,
                runner,
                textureLoader,
                positions,
                manifest,
                configuration.ManifestPath,
                configuration.InitialPosition,
                multiplyMaterial,
                buildLayerViews: false);
            await actor.BuildLayerViewsAsync(
                manifest,
                atlases,
                frameClock,
                cancellationToken);
            actor.SetVisible(true);
            return actor;
        }
        catch
        {
            if (actor is not null)
            {
                actor.Destroy();
            }
            else
            {
                view.QueueFree();
            }

            throw;
        }
    }

    private LayeredCharacterManifest GetManifest(string path) =>
        GetOrStartManifestLoad(path).GetAwaiter().GetResult();

    private Task<LayeredCharacterManifest> GetManifestAsync(
        string path,
        CancellationToken cancellationToken) =>
        GetOrStartManifestLoad(path).WaitAsync(cancellationToken);

    private Task<LayeredCharacterManifest> GetOrStartManifestLoad(string path)
    {
        lock (manifestGate)
        {
            if (manifestLoads.TryGetValue(path, out var loading))
            {
                return loading;
            }

            var json = ReadManifest(path);
            loading = Task.Run(() => LayeredCharacterManifest.Parse(json));
            manifestLoads.Add(path, loading);
            return loading;
        }
    }

    private static string ReadManifest(string path)
    {
        using var file = global::Godot.FileAccess.Open(
            path,
            global::Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            throw new ActorException(
                $"Layered character manifest could not be opened: '{path}'.");
        }

        return file.GetAsText();
    }

    private static void ValidateManifestId(
        string actorId,
        LayeredCharacterManifest manifest)
    {
        if (!string.Equals(manifest.Id, actorId, StringComparison.Ordinal))
        {
            throw new ActorDefinitionException(
                actorId,
                $"Layered character manifest ID is '{manifest.Id}'.");
        }
    }

    private static string GetDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash <= "res:/".Length)
        {
            throw new ActorException($"Layered character manifest path '{path}' is invalid.");
        }

        return path[..slash];
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
