#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Owns a hidden Direction factory environment for node-free metadata queries.
/// Factories are constructed, but this environment never creates actor instances.</summary>
public sealed class DirectionActorDefinitionEnvironment : IRuntimeService, IAsyncDisposable
{
    private readonly Node2D root;
    private readonly ActorManager manager;
    private bool disposed;

    public DirectionActorDefinitionEnvironment(Node parent, Func<ActorMountRoots, ActorManager> createManager, Vector2 referenceSize)
    {
        root = new Node2D { Name = "DirectionDefinitionMetadata", Visible = false };
        parent.AddChild(root);
        try { manager = createManager(ActorMountRoots.CreateUnder(root, referenceSize)); }
        catch { root.QueueFree(); throw; }
    }

    public ActorDefinitionResolver Resolver => manager.DefinitionResolver;
    public Task InitializeAsync(CancellationToken cancellationToken) => manager.InitializeAsync(cancellationToken);
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => new(ShutdownAsync(CancellationToken.None));

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (disposed) return;
        disposed = true;
        var failures = new List<Exception>();
        try { await manager.ShutdownAsync(cancellationToken); }
        catch (Exception error) { failures.Add(error); }
        try { manager.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        try { if (GodotObject.IsInstanceValid(root)) root.QueueFree(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Direction metadata environment cleanup failed.", failures);
    }
}
