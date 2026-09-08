#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Resources;

/// <summary>Strongly retains generated resources independently of the evictable load cache.</summary>
internal sealed class RegisteredResourceProvider : IResourceProvider
{
    private readonly Dictionary<ResourceKey, Entry> entries = new();
    public string Id => "majo.registered";

    public void Register<T>(ResourceKey key, T resource, Action<T>? release) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Path);
        ArgumentNullException.ThrowIfNull(resource);
        lock (entries)
        {
            if (!entries.TryAdd(key, new Entry(resource, release is null ? null : () => release(resource))))
                throw new MajoResourceException($"Resource '{key}' is already registered. Unregister it before replacing it.");
        }
    }

    public bool Remove(ResourceKey key)
    {
        Entry? removed;
        lock (entries)
        {
            if (!entries.Remove(key, out removed)) return false;
        }
        removed.Release?.Invoke();
        return true;
    }

    public IReadOnlyList<string> GetCandidatePaths(ResourceRequest request) => Array.Empty<string>();
    public bool TryResolve(ResourceRequest request, out ResourceLocation? location)
    {
        lock (entries)
        {
            location = request.OverridePath is null && entries.TryGetValue(request.Key, out var entry) && request.ResourceType.IsInstanceOfType(entry.Resource)
                ? new ResourceLocation(Id, request.Key.Path) : null;
            return location is not null;
        }
    }
    public object Load(ResourceLocation location, Type resourceType)
    {
        lock (entries)
        {
            if (entries.TryGetValue(new ResourceKey(location.Path), out var entry) && resourceType.IsInstanceOfType(entry.Resource)) return entry.Resource;
        }
        throw new MajoResourceException($"Registered resource '{location.Path}' is no longer available.");
    }
    public Task<object> LoadAsync(ResourceLocation location, Type resourceType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Load(location, resourceType));
    }
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    // Runtime reset does not invalidate application registrations; scene hooks can reuse them.
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Entry[] removed;
        lock (entries) { removed = new List<Entry>(entries.Values).ToArray(); entries.Clear(); }
        List<Exception>? errors = null;
        foreach (var entry in removed)
        {
            try { entry.Release?.Invoke(); }
            catch (Exception exception) { (errors ??= new()).Add(exception); }
        }
        if (errors is not null) throw new AggregateException("Registered resource release failed.", errors);
        return Task.CompletedTask;
    }
    private sealed record Entry(object Resource, Action? Release);
}
