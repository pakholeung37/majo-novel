#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Resources.Godot;

public sealed class GodotProjectResourceProvider : IResourceProvider
{
    public const string ProviderId = "godot-project";
    private static readonly string[] DefaultTextureSourceExtensions =
        ["png", "jpg", "jpeg", "webp", "svg", "bmp", "tga"];

    private readonly string rootPath;
    private readonly IFrameClock frameClock;
    private readonly Dictionary<Type, string[]> sourceExtensions = new();
    private bool isInitialized;

    public GodotProjectResourceProvider(string rootPath, IFrameClock frameClock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!rootPath.StartsWith("res://", StringComparison.Ordinal))
        {
            throw new MajoResourceException(
                "Godot project resource root must start with 'res://'.");
        }

        this.rootPath = rootPath.TrimEnd('/');
        this.frameClock = frameClock ?? throw new ArgumentNullException(nameof(frameClock));
        sourceExtensions[typeof(Texture2D)] = DefaultTextureSourceExtensions;
    }

    public string Id => ProviderId;

    public GodotProjectResourceProvider RegisterSourceExtensions<TResource>(
        params string[] extensions)
        where TResource : class => RegisterSourceExtensions(typeof(TResource), extensions);

    public GodotProjectResourceProvider RegisterSourceExtensions(
        Type resourceType,
        params string[] extensions)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        ArgumentNullException.ThrowIfNull(extensions);
        if (isInitialized)
        {
            throw new MajoResourceException(
                "Source extensions cannot be changed after provider initialization.");
        }

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

    public bool TryResolve(ResourceRequest request, out ResourceLocation? location)
    {
        EnsureInitialized();
        foreach (var candidate in GetCandidatePaths(request))
        {
            if (!ResourceLoader.Exists(candidate, request.ResourceType.Name))
            {
                continue;
            }

            location = new ResourceLocation(Id, candidate);
            return true;
        }

        location = null;
        return false;
    }

    public object Load(ResourceLocation location, Type resourceType)
    {
        EnsureInitialized();
        var resource = ResourceLoader.Load(
            location.Path,
            resourceType.Name,
            ResourceLoader.CacheMode.Reuse) ?? throw new MajoResourceException(
            $"Godot failed to load resource '{location.Path}'.");
        return RequireType(resource, location.Path, resourceType);
    }

    public async Task<object> LoadAsync(
        ResourceLocation location,
        Type resourceType,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var error = ResourceLoader.LoadThreadedRequest(
            location.Path,
            resourceType.Name,
            useSubThreads: true,
            ResourceLoader.CacheMode.Reuse);
        var status = ResourceLoader.LoadThreadedGetStatus(location.Path);
        if (error != Error.Ok &&
            status is not ResourceLoader.ThreadLoadStatus.InProgress and
            not ResourceLoader.ThreadLoadStatus.Loaded)
        {
            throw new MajoResourceException(
                $"Godot failed to start loading resource '{location.Path}': {error}.");
        }

        while (status == ResourceLoader.ThreadLoadStatus.InProgress)
        {
            await frameClock.NextFrameAsync(cancellationToken);
            status = ResourceLoader.LoadThreadedGetStatus(location.Path);
        }

        if (status != ResourceLoader.ThreadLoadStatus.Loaded)
        {
            throw new MajoResourceException(
                $"Godot failed to load resource '{location.Path}': {status}.");
        }

        var resource = ResourceLoader.LoadThreadedGet(location.Path) ??
            throw new MajoResourceException(
                $"Godot returned null for resource '{location.Path}'.");
        return RequireType(resource, location.Path, resourceType);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = false;
        return Task.CompletedTask;
    }

    private string ResolveOverride(string overridePath) => ResourcePath.IsRooted(overridePath)
        ? overridePath.Replace('\\', '/')
        : ResourcePath.Combine(rootPath, overridePath);

    private static object RequireType(Resource resource, string path, Type expectedType)
    {
        if (expectedType.IsInstanceOfType(resource))
        {
            return resource;
        }

        throw new ResourceTypeMismatchException(path, expectedType, resource.GetType());
    }

    private static bool HasExtension(string path)
    {
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        return fileName.Contains('.', StringComparison.Ordinal);
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new MajoResourceException(
                $"Resource provider '{Id}' is not initialized.");
        }
    }
}
