#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Resources.Godot;

public sealed class GodotProjectResourceProvider : IResourceProvider
{
    public const string ProviderId = "godot-project";
    private readonly IResourcePathResolver pathResolver;
    private readonly IFrameClock frameClock;
    private bool isInitialized;

    public GodotProjectResourceProvider(string rootPath, IFrameClock frameClock)
        : this(new GodotProjectResourcePathResolver(rootPath), frameClock)
    {
    }

    public GodotProjectResourceProvider(
        IResourcePathResolver pathResolver,
        IFrameClock frameClock)
    {
        this.pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        this.frameClock = frameClock ?? throw new ArgumentNullException(nameof(frameClock));
    }

    public string Id => ProviderId;

    public GodotProjectResourceProvider RegisterSourceExtensions<TResource>(
        params string[] extensions)
        where TResource : class => RegisterSourceExtensions(typeof(TResource), extensions);

    public GodotProjectResourceProvider RegisterSourceExtensions(
        Type resourceType,
        params string[] extensions)
    {
        if (isInitialized)
        {
            throw new MajoResourceException(
                "Source extensions cannot be changed after provider initialization.");
        }

        if (pathResolver is not GodotProjectResourcePathResolver defaultResolver)
        {
            throw new MajoResourceException(
                "Source extensions can only be registered on the default Godot project path resolver.");
        }

        defaultResolver.RegisterSourceExtensions(resourceType, extensions);
        return this;
    }

    public IReadOnlyList<string> GetCandidatePaths(ResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return pathResolver.GetCandidatePaths(request) ??
            throw new MajoResourceException(
                $"Resource path resolver '{pathResolver.GetType().Name}' returned null.");
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
        using var timing = Diagnostics.RuntimePerformanceTrace.Source.StartActivity("resource.load.sync");
        timing?.SetTag("path", location.Path);
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
        using var timing = Diagnostics.RuntimePerformanceTrace.Source.StartActivity("resource.load.async");
        timing?.SetTag("path", location.Path);
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

        using var retrieve = Diagnostics.RuntimePerformanceTrace.Source.StartActivity("resource.retrieve.sync");
        retrieve?.SetTag("path", location.Path);
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

    private static Resource RequireType(Resource resource, string path, Type expectedType)
    {
        if (expectedType.IsInstanceOfType(resource))
        {
            return resource;
        }

        throw new ResourceTypeMismatchException(path, expectedType, resource.GetType());
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
