#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;

namespace Majo.Demos.Live2D.Actors;

/// <summary>
/// Bridges transient Live2D commands to the actor instance owned by Direction.
/// Durable state such as expression and transform continues to live in Majo.
/// </summary>
public sealed class Live2DActorRegistry : IRuntimeService
{
    private readonly Dictionary<string, List<Live2DCharacterActor>> actors =
        new(StringComparer.Ordinal);

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        actors.Clear();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        actors.Clear();
        return Task.CompletedTask;
    }

    internal void Register(Live2DCharacterActor actor)
    {
        if (!actors.TryGetValue(actor.Id, out var instances))
        {
            instances = [];
            actors.Add(actor.Id, instances);
        }

        instances.Add(actor);
    }

    internal void Unregister(Live2DCharacterActor actor)
    {
        if (!actors.TryGetValue(actor.Id, out var instances))
        {
            return;
        }

        instances.Remove(actor);
        if (instances.Count == 0)
        {
            actors.Remove(actor.Id);
        }
    }

    public Task PlayMotionAsync(
        string actorId,
        string motion,
        bool wait,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(motion);
        if (!actors.TryGetValue(actorId, out var instances))
        {
            throw new ActorException($"Live2D actor '{actorId}' has not been created yet.");
        }

        var actor = instances.LastOrDefault(candidate => candidate.Visible) ?? instances.Last();
        return actor.PlayMotionAsync(motion, wait, cancellationToken);
    }
}
