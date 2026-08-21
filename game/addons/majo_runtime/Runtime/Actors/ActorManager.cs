#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Actors;

public sealed class ActorManager : IActorManager
{
    private readonly Dictionary<string, ActorDefinition> definitions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActorFactory> factories =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActor> actors =
        new(StringComparer.Ordinal);
    private bool isInitialized;

    public ActorManager(
        IEnumerable<ActorDefinition> definitions,
        IEnumerable<IActorFactory>? factories = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!this.definitions.TryAdd(definition.Id, definition))
            {
                throw new DuplicateActorIdException(definition.Id);
            }
        }

        if (factories is null)
        {
            return;
        }

        foreach (var factory in factories)
        {
            RegisterFactory(factory);
        }
    }

    public IReadOnlyCollection<IActor> Actors => actors.Values.ToArray();

    public ActorManager RegisterFactory(IActorFactory factory, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(factory.FactoryId);
        if (isInitialized)
        {
            throw new ActorException("Actor factories must be registered before initialization.");
        }

        if (!replace && factories.ContainsKey(factory.FactoryId))
        {
            throw new DuplicateActorFactoryException(factory.FactoryId);
        }

        factories[factory.FactoryId] = factory;
        return this;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized)
        {
            return Task.CompletedTask;
        }

        foreach (var definition in definitions.Values)
        {
            if (!factories.ContainsKey(definition.FactoryId))
            {
                throw new ActorFactoryNotFoundException(definition.FactoryId, definition.Id);
            }
        }

        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        DestroyAll();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        DestroyAll();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public IActor Create(string actorId)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actors.ContainsKey(actorId))
        {
            throw new DuplicateActorIdException(actorId);
        }

        if (!definitions.TryGetValue(actorId, out var definition))
        {
            throw new ActorNotFoundException(actorId);
        }

        if (!factories.TryGetValue(definition.FactoryId, out var factory))
        {
            throw new ActorFactoryNotFoundException(definition.FactoryId, actorId);
        }

        var actor = factory.Create(definition) ?? throw new ActorException(
            $"Actor factory '{factory.FactoryId}' returned null for '{actorId}'.");
        if (!string.Equals(actor.Id, actorId, StringComparison.Ordinal))
        {
            actor.Destroy();
            throw new ActorException(
                $"Actor factory '{factory.FactoryId}' returned ID '{actor.Id}' for '{actorId}'.");
        }

        actors.Add(actorId, actor);
        return actor;
    }

    public TActor Get<TActor>(string actorId) where TActor : class, IActor
    {
        EnsureInitialized();
        if (!actors.TryGetValue(actorId, out var actor))
        {
            throw new ActorNotFoundException(actorId);
        }

        if (actor is not TActor typedActor)
        {
            throw new ActorException(
                $"Actor '{actorId}' is '{actor.GetType().FullName}', not '{typeof(TActor).FullName}'.");
        }

        return typedActor;
    }

    public bool TryGet(string actorId, out IActor? actor)
    {
        EnsureInitialized();
        return actors.TryGetValue(actorId, out actor);
    }

    public bool Remove(string actorId)
    {
        EnsureInitialized();
        if (!actors.Remove(actorId, out var actor))
        {
            return false;
        }

        actor.Destroy();
        return true;
    }

    private void DestroyAll()
    {
        var snapshot = actors.Values.Reverse().ToArray();
        actors.Clear();
        var exceptions = new List<Exception>();
        foreach (var actor in snapshot)
        {
            try
            {
                actor.Destroy();
            }
            catch (Exception exception)
            {
                exceptions.Add(new ActorException(
                    $"Actor '{actor.Id}' failed to destroy.", exception));
            }
        }

        if (exceptions.Count > 0)
        {
            throw new AggregateException("One or more actors failed to destroy.", exceptions);
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new ActorException("ActorManager is not initialized.");
        }
    }
}
