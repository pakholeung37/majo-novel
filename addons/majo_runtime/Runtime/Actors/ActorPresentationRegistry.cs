#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Majo.Runtime.Actors;

internal sealed class ActorPresentationRegistry(IActorPresentationHost? host)
{
    private static readonly ConditionalWeakTable<IActor, ActorPresentationRegistry> Owners = new();
    private static readonly object OwnerGate = new();
    private readonly object gate = new();
    private readonly Dictionary<IBackgroundActor, string> backgroundSlots = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IActor> presented = new(ReferenceEqualityComparer.Instance);

    public void Claim(IActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (OwnerGate)
        {
            if (Owners.TryGetValue(actor, out _))
                throw new ActorException($"Actor '{actor.Id}' is already managed. Factories must return a new instance.");
            Owners.Add(actor, this);
        }
    }

    public void Attach(IActor actor)
    {
        bool attach;
        lock (gate)
        {
            if (actor is IBackgroundActor background) backgroundSlots.TryAdd(background, BackgroundSlotIds.Main);
            attach = host is not null && presented.Add(actor);
        }
        if (attach) host!.Attach(actor);
    }

    public string GetBackgroundSlot(IBackgroundActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (gate) return backgroundSlots.TryGetValue(actor, out var slot) ? slot :
            throw new ActorException($"Background '{actor.Id}' is not owned by this manager.");
    }

    public void SetBackgroundSlot(IBackgroundActor actor, string slot)
    {
        var normalized = BackgroundSlotIds.Normalize(slot);
        lock (gate)
        {
            if (!backgroundSlots.ContainsKey(actor))
                throw new ActorException($"Background '{actor.Id}' is not owned by this manager.");
            backgroundSlots[actor] = normalized;
        }
    }

    public void Destroy(IActor actor)
    {
        Exception? detachFailure = null;
        try { Release(actor); }
        catch (Exception failure) { detachFailure = failure; }
        try { actor.Destroy(); }
        catch (Exception failure) when (detachFailure is not null)
        { throw new AggregateException("Actor presentation and content cleanup failed.", detachFailure, failure); }
        if (detachFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(detachFailure).Throw();
    }

    private void Release(IActor actor)
    {
        bool detach;
        lock (gate)
        {
            if (actor is IBackgroundActor background) backgroundSlots.Remove(background);
            detach = presented.Remove(actor);
        }
        try { if (detach) host?.Detach(actor); }
        finally
        {
            lock (OwnerGate)
                if (Owners.TryGetValue(actor, out var owner) && ReferenceEquals(owner, this)) Owners.Remove(actor);
        }
    }
}
