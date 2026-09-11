#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;

namespace Majo.Demos.Manosaba.Actors.Layered;

/// <summary>Runtime-owned metadata cache. Never stores scene nodes or materials.</summary>
public sealed class LayeredCharacterManifestCache : IRuntimeService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Task<LayeredCharacterManifest>> loads = new(StringComparer.Ordinal);
    private readonly Func<string, Task<LayeredCharacterManifest>> load;

    public LayeredCharacterManifestCache() : this(LoadAsync) { }

    public LayeredCharacterManifestCache(Func<string, Task<LayeredCharacterManifest>> load) =>
        this.load = load ?? throw new ArgumentNullException(nameof(load));

    public LayeredCharacterManifest Get(string path) => GetOrStart(path).GetAwaiter().GetResult();

    public Task<LayeredCharacterManifest> GetAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return GetOrStart(path).WaitAsync(token);
    }

    private Task<LayeredCharacterManifest> GetOrStart(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (gate)
        {
            if (loads.TryGetValue(path, out var task) && !task.IsFaulted && !task.IsCanceled) return task;
            // Read on the caller's Godot thread; only pure JSON parsing runs in the background.
            task = load(path);
            loads[path] = task;
            return task;
        }
    }

    private static Task<LayeredCharacterManifest> LoadAsync(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null) throw new ActorException($"Layered character manifest could not be opened: '{path}'.");
        var json = file.GetAsText();
        return Task.Run(() => LayeredCharacterManifest.Parse(json));
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Immutable project metadata survives scene resets, but never another runtime.
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        lock (gate) loads.Clear();
        return Task.CompletedTask;
    }
}
