#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Resolves reusable objects/&lt;asset&gt;.json definitions independently of instance IDs.</summary>
public sealed class SceneObjectActorFactory : IConventionActorFactory
{
    private readonly Node2D host;
    private readonly IActorTextureLoader textures;
    private readonly string rootPath;
    private readonly string objectsPath;
    private readonly Vector2I resolution;
    private readonly IActorAppearanceTransitionService transitions;

    public SceneObjectActorFactory(Node2D host, IActorTextureLoader textures,
        ActorResourceSettings resources, Vector2I resolution,
        IActorAppearanceTransitionService? transitions = null)
    {
        this.host = host;
        this.textures = textures;
        rootPath = resources.RootPath.TrimEnd('/');
        objectsPath = resources.ObjectsPath.Trim('/');
        this.resolution = resolution;
        this.transitions = transitions ?? new GodotActorAppearanceTransitionService();
    }

    public string FactoryId => ActorFactoryIds.SceneObject;

    public string GetInitialAppearance(ActorDefinition definition) =>
        definition.GetFactoryData<TextureActorDefinitionData>().DefaultAppearance;

    public bool TryResolveDefinition(ActorSpawnRequest request, out ActorDefinition definition)
    {
        definition = null!;
        if (request.FallbackFactoryId != FactoryId) return false;
        if (string.IsNullOrWhiteSpace(request.DefinitionId))
            throw new ActorDefinitionException(request.ActorId, "Object creation requires asset.");
        var asset = new ResourceKey(request.DefinitionId).Path;
        var path = $"{rootPath}/{objectsPath}/{asset}.json";
        if (!FileAccess.FileExists(path))
            throw new ActorDefinitionException(asset, $"Object definition '{path}' does not exist.");
        using var json = JsonDocument.Parse(FileAccess.GetFileAsString(path));
        var root = json.RootElement;
        var selectedFactory = root.TryGetProperty("factory", out var factory) ? factory.GetString() : FactoryId;
        if (string.IsNullOrWhiteSpace(selectedFactory))
            throw new ActorDefinitionException(asset, "Object factory cannot be empty.");
        var appearances = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var appearance in root.GetProperty("appearances").EnumerateObject())
        {
            var texture = appearance.Value.GetString() ?? throw new ActorDefinitionException(asset, "Appearance path is null.");
            appearances.Add(appearance.Name, texture.StartsWith("res://", StringComparison.Ordinal)
                ? texture : $"{rootPath}/{objectsPath}/{texture}");
        }
        if (!appearances.ContainsKey("Default"))
            throw new ActorDefinitionException(asset, "Object definition requires a Default appearance.");
        var anchor = new Vector2(0.5f, 0.5f);
        if (root.TryGetProperty("anchor", out var point))
            anchor = new Vector2(point[0].GetSingle(), point[1].GetSingle());
        if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y))
            throw new ActorDefinitionException(asset, "Object anchor must be finite.");
        definition = new ActorDefinition(request.ActorId, selectedFactory,
            new TextureActorDefinitionData(asset, objectsPath, "Default", appearances)
            { ResourceId = asset, Anchor = anchor }, asset);
        return true;
    }

    public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance) =>
        new[] { ResourcePreloadEntry.Create<Texture2D>(
            definition.GetFactoryData<TextureActorDefinitionData>().GetAppearanceResourceKey(
                definition.DefinitionId, appearance),
            definition.GetFactoryData<TextureActorDefinitionData>().GetAppearanceOverride(appearance)) };

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public Task<IActor> CreateAsync(
        ActorDefinition definition,
        ActorCreationMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = new Sprite2D { Name = definition.Id, Visible = false };
        host.AddChild(view);
        try
        {
            var actor = new SceneObjectActor(definition, view, textures, resolution, transitions);
            actor.SetVisible(mode == ActorCreationMode.Active);
            return Task.FromResult<IActor>(actor);
        }
        catch { view.QueueFree(); throw; }
    }
}
