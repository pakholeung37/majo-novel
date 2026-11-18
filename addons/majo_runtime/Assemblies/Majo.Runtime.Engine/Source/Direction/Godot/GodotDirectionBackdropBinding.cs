#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionBackdropBinding : IAsyncDisposable
{
    public Node2D Root { get; }
    public IActorManager Manager { get; }
    public IBackgroundActor Actor { get; }
    public GodotBackgroundRaster? Current { get; set; }
    public long LocaleRevision { get; set; }
    public static int Count { get; private set; }
    private bool disposed;

    private GodotDirectionBackdropBinding(Node2D root, IActorManager manager, IBackgroundActor actor)
    { Root = root; Manager = manager; Actor = actor; Count++; }

    public static async Task<GodotDirectionBackdropBinding> CreateAsync(Node parent, Vector2 size,
        BackdropState state, Func<ActorMountRoots, IActorManager> createManager, CancellationToken token)
    {
        var root = new Node2D { Name = "BackdropBinding", Visible = false };
        parent.AddChild(root);
        IActorManager? manager = null;
        try
        {
            manager = createManager(ActorMountRoots.CreateUnder(root, size));
            await manager.InitializeAsync(token);
            var actor = await manager.GetOrCreateAsync(new ActorSpawnRequest("main", state.FactoryId, state.Appearance,
                requiredFactoryId: state.FactoryId, creationMode: ActorCreationMode.Dormant)
            { DefinitionId = state.DefinitionId, UseBackdropResourceConvention = true }, token);
            token.ThrowIfCancellationRequested();
            if (actor is not IBackgroundActor background || actor.FactoryId != state.FactoryId ||
                actor.DefinitionId != state.DefinitionId || !root.IsAncestorOf(background.SceneNode))
                throw new DirectionException("Background factory returned an incompatible instance or mounted outside its environment.");
            GodotBackgroundRaster.ValidateTree(background.SceneNode);
            actor.SetTint(Colors.White);
            actor.SetOpacity(1);
            return new GodotDirectionBackdropBinding(root, manager, background);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            try { if (manager is not null) await manager.ShutdownAsync(CancellationToken.None); }
            catch (Exception error) { failures.Add(error); }
            try { (manager as IDisposable)?.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            root.QueueFree();
            if (failures.Count > 1) throw new AggregateException("Background binding creation and cleanup failed.", failures);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        Count--;
        var failures = new List<Exception>();
        try { Current?.Dispose(); Current = null; }
        catch (Exception error) { failures.Add(error); }
        try { await Manager.ShutdownAsync(CancellationToken.None); }
        catch (Exception error) { failures.Add(error); }
        try { (Manager as IDisposable)?.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        try { if (GodotObject.IsInstanceValid(Root)) Root.QueueFree(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Background binding cleanup failed.", failures);
    }
}
