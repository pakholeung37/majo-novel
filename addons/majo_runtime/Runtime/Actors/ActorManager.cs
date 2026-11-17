#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.State;

namespace Majo.Runtime.Actors;

public sealed class ActorManager : IActorManager, IActorReplacementSource, IStateMutationParticipant, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, ActorDefinition> definitions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActorDefinition> unsupportedDefinitions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActorFactory> factories =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActor> actors =
        new(StringComparer.Ordinal);
    private readonly List<string> actorOrder = new();
    private readonly ActorCreationQueue creations = new();
    private readonly ActorPresentationRegistry presentation;
    private readonly ActorLocaleRefresher localeRefresh;
    private readonly ActorCreationConventions conventions;
    public ActorDefinitionResolver DefinitionResolver { get; }
    public bool RefreshBackgroundsOnLocaleChange { get => localeRefresh.RefreshBackgrounds; set => localeRefresh.RefreshBackgrounds = value; }
    private bool isInitialized;
    private StateMutationGate? mutationGate;

    public void BindStateMutationGate(StateMutationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (mutationGate is not null && !ReferenceEquals(mutationGate, gate))
            throw new StateCompatibilityException("Actor manager is already bound to another runtime's state mutation gate.");
        mutationGate = gate;
        lock (this.gate)
        {
            foreach (var actor in actors.Values) BindActor(actor);
        }
    }

    public ActorManager(
        IEnumerable<ActorDefinition> definitions,
        IEnumerable<IActorFactory>? factories = null,
        ILocaleManager? locales = null,
        IRuntimeLogger? logger = null,
        ActorCreationConventions? conventions = null,
        IEnumerable<ActorDefinition>? unsupportedDefinitions = null,
        IActorPresentationHost? presentationHost = null)
    {
        presentation = new ActorPresentationRegistry(presentationHost);
        var runtimeLogger = logger ?? NullRuntimeLogger.Instance;
        localeRefresh = new ActorLocaleRefresher(locales, () => Actors, runtimeLogger);
        this.conventions = conventions ?? new ActorCreationConventions();
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!this.definitions.TryAdd(definition.Id, definition))
            {
                throw new DuplicateActorIdException(definition.Id);
            }
        }
        foreach (var definition in unsupportedDefinitions ?? Array.Empty<ActorDefinition>())
        {
            if (this.definitions.ContainsKey(definition.Id) ||
                !this.unsupportedDefinitions.TryAdd(definition.Id, definition))
                throw new DuplicateActorIdException(definition.Id);
        }

        DefinitionResolver = new ActorDefinitionResolver(this.definitions, this.factories,
            this.unsupportedDefinitions, this.conventions, runtimeLogger);

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
                return actorOrder.Select(id => actors[id]).ToArray();
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
            if (!factories.ContainsKey(definition.FactoryId))
                throw new ActorFactoryNotFoundException(definition.FactoryId, definition.Id);
        }

        localeRefresh.Attach();

        isInitialized = true;
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        var cancellationFailure = await creations.CancelAndDrainAsync(cancellationToken);
        try { DestroyAll(cancellationFailure); }
        finally { creations.Resume(); }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cancellationFailure = await creations.CancelAndDrainAsync(cancellationToken);
        try { DestroyAll(cancellationFailure); }
        finally
        {
            localeRefresh.Dispose();
            isInitialized = false;
            creations.Dispose();
        }
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

            if (creations.TryGet(actorId, out var pending))
            {
                creation = pending.Task;
            }
            else
            {
                if (!definitions.TryGetValue(actorId, out var definition))
                {
                    ThrowIfUnsupported(actorId);
                    throw new ActorNotFoundException(actorId);
                }

                if (!factories.TryGetValue(definition.FactoryId, out var factory))
                {
                    throw new ActorFactoryNotFoundException(definition.FactoryId, actorId);
                }

                creation = StartCreation(actorId, definition, factory, ActorCreationMode.Active);
            }
        }

        return creation.WaitAsync(cancellationToken);
    }

    public Task<IActor> GetOrCreateAsync(
        ActorSpawnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        Task<IActor> creation;
        lock (gate)
        {
            if (actors.TryGetValue(request.ActorId, out var existing))
            {
                ValidateExistingActor(request, existing);
                return Task.FromResult(existing);
            }

            if (creations.TryGet(request.ActorId, out var pending))
            {
                ValidateRequiredFactory(request, pending.FactoryId);
                ValidateDefinitionIdentity(request, pending.DefinitionId);
                return pending.Task.WaitAsync(cancellationToken);
            }

            var definition = ResolveDefinition(request);
            if (!factories.TryGetValue(definition.FactoryId, out var factory))
            {
                throw new ActorFactoryNotFoundException(definition.FactoryId, request.ActorId);
            }

            creation = StartCreation(request.ActorId, definition, factory, request.CreationMode);
        }

        return creation.WaitAsync(cancellationToken);
    }

    public TActor GetActor<TActor>(string actorId) where TActor : class, IActor
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
        return Mutate(() =>
        {
            IActor? actor;
            lock (gate)
            {
                if (!actors.Remove(actorId, out actor)) return false;
                actorOrder.Remove(actorId);
            }
            DestroyOwnedActor(actor);
            return true;
        });
    }

    public bool CanCreate(ActorSpawnRequest request) => DefinitionResolver.CanResolve(request);

    public IReadOnlyList<Majo.Runtime.Resources.ResourcePreloadEntry> GetPreloadResources(ActorSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (actors.TryGetValue(request.ActorId, out var existing))
            {
                ValidateExistingActor(request, existing);
                request = request with { DefinitionId = existing.DefinitionId };
                if (factories.TryGetValue(existing.FactoryId, out var existingFactory) &&
                    !existingFactory.PreloadExistingAppearances)
                    return Array.Empty<Majo.Runtime.Resources.ResourcePreloadEntry>();
            }
            return DefinitionResolver.GetPreloadResources(request);
        }
    }

    private ActorDefinition ResolveDefinition(ActorSpawnRequest request) => DefinitionResolver.Resolve(request);

    private void ThrowIfUnsupported(string definitionId)
    {
        if (unsupportedDefinitions.TryGetValue(definitionId, out var unsupported))
            throw new ActorDefinitionException(definitionId,
                $"Factory '{unsupported.FactoryId}' does not support this actor manager environment.");
    }

    private static void ValidateRequiredFactory(
        ActorSpawnRequest request,
        string factoryId)
    {
        if (request.RequiredFactoryId is not null &&
            !string.Equals(
                factoryId,
                request.RequiredFactoryId,
                StringComparison.Ordinal))
        {
            throw new ActorFactoryConflictException(
                request.ActorId,
                factoryId,
                request.RequiredFactoryId);
        }
    }

    private static void ValidateDefinitionIdentity(ActorSpawnRequest request, string definitionId)
    {
        if (request.DefinitionId is not null &&
            !string.Equals(request.DefinitionId, definitionId, StringComparison.Ordinal))
            throw new ActorDefinitionException(
                request.ActorId,
                $"Actor instance already uses definition '{definitionId}', not '{request.DefinitionId}'.");
    }

    private static void ValidateExistingActor(ActorSpawnRequest request, IActor actor)
    {
        ValidateRequiredFactory(request, actor.FactoryId);
        ValidateDefinitionIdentity(request, actor.DefinitionId);
    }


    public async Task<IPreparedActorReplacement> PrepareReplacementAsync(
        IReadOnlyList<ActorSpawnRequest> requests, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (requests.Select(request => request.ActorId).Distinct(StringComparer.Ordinal).Count() != requests.Count)
            throw new ActorException("An actor replacement contains duplicate IDs.");
        var candidates = new List<IActor>();
        try
        {
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = ResolveDefinition(request);
                if (!factories.TryGetValue(definition.FactoryId, out var factory))
                    throw new ActorFactoryNotFoundException(definition.FactoryId, request.ActorId);
                factory.ValidateDefinition(definition);
                var candidate = await factory.CreateAsync(definition, ActorCreationMode.Dormant, cancellationToken);
                ClaimCreatedActor(candidate);
                candidates.Add(candidate);
                ValidateCreatedActor(request.ActorId, definition, factory, candidate);
                BindActor(candidate);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return new PreparedReplacement(this, candidates.ToArray());
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var candidate in candidates)
                try
                {
                    DestroyOwnedActor(candidate);
                }
                catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count > 1) throw new AggregateException("Actor preparation and cleanup failed.", failures);
            throw;
        }
    }

    private sealed class PreparedReplacement(ActorManager owner, IActor[] candidates) : IPreparedActorReplacement
    {
        private IActor[]? retired;
        private bool disposed;
        public IReadOnlyList<IActor> Actors { get; } = Array.AsReadOnly(candidates);

        public void Commit()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (retired is not null) throw new ActorException("Actor replacement was already committed.");
            owner.Mutate(() =>
            {
                lock (owner.gate)
                {
                    if (owner.creations.Count != 0)
                        throw new ActorException("Actor creation must quiesce before replacement.");
                    retired = owner.actorOrder.Select(id => owner.actors[id]).ToArray();
                    owner.actors.Clear();
                    owner.actorOrder.Clear();
                    foreach (var actor in candidates)
                    {
                        owner.actors.Add(actor.Id, actor);
                        owner.actorOrder.Add(actor.Id);
                    }
                    foreach (var actor in retired) actor.SetVisible(false);
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            List<Exception>? failures = null;
            foreach (var actor in retired ?? candidates)
                try { owner.DestroyOwnedActor(actor); }
                catch (Exception error) { (failures ??= new()).Add(error); }
            if (failures is not null) throw new AggregateException("Actor replacement cleanup failed.", failures);
            return ValueTask.CompletedTask;
        }
    }

    private void DestroyAll(Exception? lifecycleFailure = null)
    {
        IActor[] snapshot;
        lock (gate)
        {
            snapshot = actorOrder.AsEnumerable().Reverse().Select(id => actors[id]).ToArray();
            actors.Clear();
            actorOrder.Clear();
        }
        var exceptions = new List<Exception>();
        if (lifecycleFailure is not null) exceptions.Add(lifecycleFailure);
        foreach (var actor in snapshot)
        {
            try
            {
                DestroyOwnedActor(actor);
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

    // Called while the active inventory is locked; the queue owns the shared job and its generation.
    private Task<IActor> StartCreation(string actorId, ActorDefinition definition, IActorFactory factory, ActorCreationMode mode)
    {
        factory.ValidateDefinition(definition);
        return creations.Start(actorId, definition, token => CreateAndPublishAsync(actorId, definition, factory, mode, token));
    }

    private async Task<IActor> CreateAndPublishAsync(string actorId, ActorDefinition definition,
        IActorFactory factory, ActorCreationMode mode, CancellationToken token)
    {
        IActor? actor = null;
        var claimed = false;
        try
        {
            actor = await factory.CreateAsync(definition, mode, token);
            ClaimCreatedActor(actor);
            claimed = true;
            token.ThrowIfCancellationRequested();
            ValidateCreatedActor(actorId, definition, factory, actor);
            BindActor(actor);
            await MutateAsync(_ =>
            {
                lock (gate) { actors.Add(actorId, actor); actorOrder.Add(actorId); }
                return Task.CompletedTask;
            }, token);
            return actor;
        }
        catch (Exception failure)
        {
            if (actor is not null && claimed)
            {
                try { DestroyOwnedActor(actor); }
                catch (Exception cleanup) { throw new AggregateException("Actor creation and cleanup failed.", failure, cleanup); }
            }
            throw;
        }
    }

    private static void ValidateCreatedActor(
        string actorId,
        ActorDefinition definition,
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
        if (!string.Equals(actor.FactoryId, factory.FactoryId, StringComparison.Ordinal))
            throw new ActorException(
                $"Actor factory '{factory.FactoryId}' returned actor with factory ID '{actor.FactoryId}'.");
        if (!string.Equals(actor.DefinitionId, definition.DefinitionId, StringComparison.Ordinal))
            throw new ActorException(
                $"Actor factory '{factory.FactoryId}' returned definition ID '{actor.DefinitionId}' for '{definition.DefinitionId}'.");
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new ActorException("ActorManager is not initialized.");
        }
    }

    private void ClaimCreatedActor(IActor actor) => presentation.Claim(actor);

    private void BindActor(IActor actor)
    {
        if (actor is not IStateMutationParticipant participant)
            throw new StateCompatibilityException($"Actor '{actor.Id}' does not implement the persistent write gate contract.");
        if (mutationGate is not null) participant.BindStateMutationGate(mutationGate);
        presentation.Attach(actor);
    }

    public string GetBackgroundSlot(IBackgroundActor actor) => presentation.GetBackgroundSlot(actor);
    public void SetBackgroundSlot(IBackgroundActor actor, string slot) => Mutate(() => presentation.SetBackgroundSlot(actor, slot));
    private void DestroyOwnedActor(IActor actor) => presentation.Destroy(actor);

    private void Mutate(Action mutation)
    {
        if (mutationGate is null) mutation();
        else mutationGate.Write(mutation);
    }

    private T Mutate<T>(Func<T> mutation) =>
        mutationGate is null ? mutation() : mutationGate.Write(mutation);

    private Task MutateAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken) => mutationGate is null
        ? mutation(cancellationToken)
        : mutationGate.WriteAsync(mutation, cancellationToken);

    public void Dispose()
    {
        localeRefresh.Dispose();
        creations.Dispose();
    }
}
