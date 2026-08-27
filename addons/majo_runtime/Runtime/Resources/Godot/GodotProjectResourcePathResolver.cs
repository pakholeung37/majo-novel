#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Majo.Runtime.Resources.Godot;

/// <summary>
/// Default convention-based resolver for resources stored in a Godot project.
/// </summary>
public sealed class GodotProjectResourcePathResolver : IResourcePathResolver
{
    private static readonly string[] DefaultTextureSourceExtensions =
        ["png", "jpg", "jpeg", "webp", "svg", "bmp", "tga"];

    private readonly string rootPath;
    private readonly Dictionary<Type, string[]> sourceExtensions = new();

    public GodotProjectResourcePathResolver(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!rootPath.StartsWith("res://", StringComparison.Ordinal))
        {
            throw new MajoResourceException(
                "Godot project resource root must start with 'res://'.");
        }

        this.rootPath = rootPath.TrimEnd('/');
        sourceExtensions[typeof(Texture2D)] = DefaultTextureSourceExtensions;
    }

    public GodotProjectResourcePathResolver RegisterSourceExtensions<TResource>(
        params string[] extensions)
        where TResource : class => RegisterSourceExtensions(typeof(TResource), extensions);

    public GodotProjectResourcePathResolver RegisterSourceExtensions(
        Type resourceType,
        params string[] extensions)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(extensions);
        var normalized = extensions
            .Select(extension => extension?.Trim().TrimStart('.') ?? string.Empty)
            .Where(extension => extension.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "At least one source extension is required.", nameof(extensions));
        }

        sourceExtensions[resourceType] = sourceExtensions.TryGetValue(
            resourceType,
            out var existing)
            ? existing.Concat(normalized).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : normalized;
        return this;
    }

    public IReadOnlyList<string> GetCandidatePaths(ResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.OverridePath))
        {
            return [ResolveOverride(request.OverridePath)];
        }

        var basePath = ResourcePath.Combine(rootPath, request.Key.Path);
        if (HasExtension(basePath))
        {
            return [basePath];
        }

        var registeredExtensions = sourceExtensions.TryGetValue(
            request.ResourceType,
            out var configured)
            ? configured
            : Array.Empty<string>();
        var extensions = registeredExtensions
            .Concat(ResourceLoader.GetRecognizedExtensionsForType(request.ResourceType.Name))
            .Select(extension => extension.TrimStart('.'))
            .Where(extension => extension.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return extensions.Select(extension => $"{basePath}.{extension}").ToArray();
    }

    private string ResolveOverride(string overridePath) => ResourcePath.IsRooted(overridePath)
        ? overridePath.Replace('\\', '/')
        : ResourcePath.Combine(rootPath, overridePath);

    private static bool HasExtension(string path)
    {
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        return fileName.Contains('.', StringComparison.Ordinal);
    }
}
