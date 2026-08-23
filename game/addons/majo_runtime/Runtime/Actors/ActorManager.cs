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
    private readonly object gate = new();
    private readonly Dictionary<string, ActorDefinition> definitions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActorFactory> factories =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActor> actors =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IActor>> pendingActors =
        new(StringComparer.Ordinal);
    private CancellationTokenSource actorLifetime = new();
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

    public IReadOnlyCollection<IActor> Actors
    {
        get
        {
            lock (gate)
            {
                return actors.Values.ToArray();
            }
        }
    }

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
            if (!factories.TryGetValue(definition.FactoryId, out var factory))
            {
                throw new ActorFactoryNotFoundException(definition.FactoryId, definition.Id);
            }

            factory.ValidateDefinition(definition);
        }

        isInitialized = true;
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        await CancelPendingCreationsAsync(cancellationToken);
        DestroyAll();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await CancelPendingCreationsAsync(cancellationToken, replaceLifetime: false);
        DestroyAll();
        isInitialized = false;
        actorLifetime.Dispose();
    }

    public IActor Create(string actorId)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        lock (gate)
        {
            if (actors.ContainsKey(actorId) || pendingActors.ContainsKey(actorId))
            {
                throw new DuplicateActorIdException(actorId);
            }
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

        lock (gate)
        {
            actors.Add(actorId, actor);
        }
        return actor;
    }

    public Task<IActor> CreateAsync(
        string actorId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        Task<IActor> creation;
        lock (gate)
        {
            if (actors.ContainsKey(actorId))
            {
                throw new DuplicateActorIdException(actorId);
            }

            if (!pendingActors.TryGetValue(actorId, out creation!))
            {
                if (!definitions.TryGetValue(actorId, out var definition))
                {
                    throw new ActorNotFoundException(actorId);
                }

                if (!factories.TryGetValue(definition.FactoryId, out var factory))
                {
                    throw new ActorFactoryNotFoundException(definition.FactoryId, actorId);
                }

                var completion = new TaskCompletionSource<IActor>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                creation = completion.Task;
                pendingActors.Add(actorId, creation);
                _ = CompleteCreationAsync(
                    actorId,
                    definition,
                    factory,
                    completion,
                    actorLifetime.Token);
            }
        }

        return creation.WaitAsync(cancellationToken);
    }

    public TActor Get<TActor>(string actorId) where TActor : class, IActor
    {
        EnsureInitialized();
        IActor actor;
        lock (gate)
        {
            if (!actors.TryGetValue(actorId, out actor!))
            {
                throw new ActorNotFoundException(actorId);
            }
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
        lock (gate)
        {
            return actors.TryGetValue(actorId, out actor);
        }
    }

    public bool Remove(string actorId)
    {
        EnsureInitialized();
        IActor? actor;
        lock (gate)
        {
            if (!actors.Remove(actorId, out actor))
            {
                return false;
            }
        }

        actor.Destroy();
        return true;
    }

    public bool CanCreate(string actorId, string factoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryId);
        return definitions.TryGetValue(actorId, out var definition) &&
            string.Equals(definition.FactoryId, factoryId, StringComparison.Ordinal) &&
            factories.ContainsKey(factoryId);
    }

    private void DestroyAll()
    {
        IActor[] snapshot;
        lock (gate)
        {
            snapshot = actors.Values.Reverse().ToArray();
            actors.Clear();
        }
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

    private async Task CompleteCreationAsync(
        string actorId,
        ActorDefinition definition,
        IActorFactory factory,
        TaskCompletionSource<IActor> completion,
        CancellationToken cancellationToken)
    {
        IActor? actor = null;
        try
        {
            actor = await factory.CreateAsync(definition, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCreatedActor(actorId, factory, actor);
            lock (gate)
            {
                actors.Add(actorId, actor);
            }

            completion.TrySetResult(actor);
            actor = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            actor?.Destroy();
            lock (gate)
            {
                pendingActors.Remove(actorId);
            }
        }
    }

    private async Task CancelPendingCreationsAsync(
        CancellationToken cancellationToken,
        bool replaceLifetime = true)
    {
        actorLifetime.Cancel();
        Task<IActor>[] pending;
        lock (gate)
        {
            pending = pendingActors.Values.ToArray();
        }

        foreach (var creation in pending)
        {
            try
            {
                await creation.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The caller that requested creation observes the factory failure.
            }
        }

        if (replaceLifetime)
        {
            actorLifetime.Dispose();
            actorLifetime = new CancellationTokenSource();
        }
    }

    private static void ValidateCreatedActor(
        string actorId,
        IActorFactory factory,
        IActor actor)
    {
        if (actor is null)
        {
            throw new ActorException(
                $"Actor factory '{factory.FactoryId}' returned null for '{actorId}'.");
        }

        if (!string.Equals(actor.Id, actorId, StringComparison.Ordinal))
        {
            throw new ActorException(
                $"Actor factory '{factory.FactoryId}' returned ID '{actor.Id}' for '{actorId}'.");
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
