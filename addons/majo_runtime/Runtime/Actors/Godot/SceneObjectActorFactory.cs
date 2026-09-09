#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Resolves reusable objects/&lt;asset&gt;.json definitions independently of instance IDs.</summary>
public sealed class SceneObjectActorFactory : IConventionActorFactory
{
    private readonly Node2D host;
    private readonly IOperationRunner runner;
    private readonly IActorTextureLoader textures;
    private readonly string rootPath;
    private readonly string objectsPath;
    private readonly Vector2I resolution;
    private readonly IEasingRegistry? easings;
    private readonly ITransitionDriverRegistry? transitions;

    public SceneObjectActorFactory(Node2D host, IOperationRunner runner, IActorTextureLoader textures,
        ResourceConfiguration resources, Vector2I resolution, IEasingRegistry? easings = null,
        ITransitionDriverRegistry? transitions = null)
    {
        this.host = host;
        this.runner = runner;
        this.textures = textures;
        rootPath = resources.RootPath.TrimEnd('/');
        objectsPath = resources.ObjectsPath.Trim('/');
        this.resolution = resolution;
        this.easings = easings;
        this.transitions = transitions;
    }

    public string FactoryId => ActorFactoryIds.SceneObject;

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
            new TextureActorDefinitionData(request.ActorId, objectsPath, "Default", appearances)
            { ResourceId = asset, Anchor = anchor });
        return true;
    }

    public IReadOnlyList<ResourcePreloadEntry> GetPreloadResources(ActorDefinition definition, string appearance) =>
        new[] { ResourcePreloadEntry.Create<Texture2D>(definition.GetAppearanceResourceKey(appearance),
            definition.GetAppearanceOverride(appearance)) };

    public void ValidateDefinition(ActorDefinition definition) =>
        _ = definition.GetFactoryData<TextureActorDefinitionData>();

    public IActor Create(ActorDefinition definition)
    {
        var view = new Sprite2D { Name = definition.Id };
        host.AddChild(view);
        try { return new SceneObjectActor(definition, view, runner, textures, resolution, easings, transitions); }
        catch { view.QueueFree(); throw; }
    }
}
