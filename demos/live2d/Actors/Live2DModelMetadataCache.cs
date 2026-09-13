#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;

namespace Majo.Demos.Live2D.Actors;

/// <summary>Small, runtime-owned cache of model references; it holds no Godot resources.</summary>
public sealed class Live2DModelMetadataCache : IRuntimeService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Live2DModelMetadata> models = new(StringComparer.Ordinal);

    public Live2DModelMetadata Get(string path)
    {
        lock (gate)
        {
            if (models.TryGetValue(path, out var cached)) return cached;
            var model = Load(path);
            models.Add(path, model);
            return model;
        }
    }

    private static Live2DModelMetadata Load(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null) throw new ActorException($"Live2D model could not be opened: '{path}'.");
        try
        {
            using var json = JsonDocument.Parse(file.GetAsText());
            var references = json.RootElement.GetProperty("FileReferences");
            var textures = new List<string>();
            foreach (var texture in references.GetProperty("Textures").EnumerateArray())
            {
                var name = texture.GetString();
                if (string.IsNullOrWhiteSpace(name))
                    throw new JsonException("A texture path is empty.");
                textures.Add(name);
            }

            var expressions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Default"] = string.Empty,
            };
            if (references.TryGetProperty("Expressions", out var expressionEntries))
                foreach (var entry in expressionEntries.EnumerateArray())
                {
                    var name = entry.GetProperty("Name").GetString();
                    if (!string.IsNullOrWhiteSpace(name)) expressions[name] = name;
                }

            var motions = new Dictionary<string, Live2DMotionDefinition>(StringComparer.Ordinal);
            if (references.TryGetProperty("Motions", out var motionGroups))
                foreach (var group in motionGroups.EnumerateObject())
                {
                    var index = 0;
                    foreach (var _ in group.Value.EnumerateArray())
                    {
                        motions.Add($"{group.Name}:{index}", new Live2DMotionDefinition(group.Name, index));
                        index++;
                    }
                }

            return new Live2DModelMetadata(textures, expressions, motions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ActorException($"Live2D model '{path}' has invalid FileReferences: {exception.Message}");
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) models.Clear();
        return Task.CompletedTask;
    }
}

public sealed record Live2DModelMetadata(
    IReadOnlyList<string> Textures,
    IReadOnlyDictionary<string, string> Expressions,
    IReadOnlyDictionary<string, Live2DMotionDefinition> Motions);
