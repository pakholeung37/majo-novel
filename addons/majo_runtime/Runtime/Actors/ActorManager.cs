#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Localization;
using Majo.Runtime.State;

namespace Majo.Runtime.Actors;

public sealed class ActorManager : IActorManager, IActorReplacementSource, IStateMutationParticipant, IDisposable
{
    private static readonly ConditionalWeakTable<IActor, ActorManager> ActorOwners = new();
    private static readonly object ActorOwnerGate = new();
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
    private readonly Dictionary<string, PendingActorCreation> pendingActors =
        new(StringComparer.Ordinal);
    private CancellationTokenSource actorLifetime = new();
    private readonly ILocaleManager? locales;
    private readonly IRuntimeLogger logger;
    private readonly ActorCreationConventions conventions;
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
        IEnumerable<ActorDefinition>? unsupportedDefinitions = null)
    {
        this.locales = locales;
        this.logger = logger ?? NullRuntimeLogger.Instance;
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

        if (locales is not null)
        {
            locales.DialogueLocaleChanged += HandleDialogueLocaleChanged;
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
        if (locales is not null)
        {
            locales.DialogueLocaleChanged -= HandleDialogueLocaleChanged;
        }
        isInitialized = false;
        actorLifetime.Dispose();
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

            if (pendingActors.TryGetValue(actorId, out var pending))
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

            if (pendingActors.TryGetValue(request.ActorId, out var pending))
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
            ReleaseActor(actor);
            actor.Destroy();
            return true;
        });
    }

    public bool CanCreate(ActorSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var factoryId = request.RequiredFactoryId ?? request.FallbackFactoryId;
        if (!factories.ContainsKey(factoryId))
        {
            return false;
        }

        if (definitions.TryGetValue(request.DefinitionId ?? request.ActorId, out var definition))
        {
            return string.Equals(definition.FactoryId, factoryId, StringComparison.Ordinal);
        }

        return TryResolveConventionDefinition(request, out var inferred)
            ? string.Equals(inferred.FactoryId, factoryId, StringComparison.Ordinal)
            : IsConventionFactory(factoryId);
    }

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
            var definition = ResolveDefinition(request);
            if (!factories.TryGetValue(definition.FactoryId, out var factory))
                return Array.Empty<Majo.Runtime.Resources.ResourcePreloadEntry>();
            factory.ValidateDefinition(definition);
            var appearance = request.UseDefinitionDefaultAppearance
                ? factory.GetInitialAppearance(definition)
                : request.InitialAppearance;
            return appearance is null
                ? Array.Empty<Majo.Runtime.Resources.ResourcePreloadEntry>()
                : factory.GetPreloadResources(definition, appearance);
        }
    }

    private ActorDefinition ResolveDefinition(ActorSpawnRequest request)
    {
        var definitionId = request.DefinitionId ?? request.ActorId;
        ThrowIfUnsupported(definitionId);
        if (definitions.TryGetValue(definitionId, out var definition))
        {
            ValidateRequiredFactory(request, definition.FactoryId);
            return string.Equals(request.ActorId, definition.Id, StringComparison.Ordinal)
                ? definition
                : new ActorDefinition(
                    request.ActorId,
                    definition.FactoryId,
                    definition.FactoryData,
                    definition.DefinitionId);
        }

        if (TryResolveConventionDefinition(request, out var conventionalDefinition))
        {
            ValidateRequiredFactory(request, conventionalDefinition.FactoryId);
            logger.Debug(
                "actor",
                "actor.definition_inferred",
                $"Actor '{request.ActorId}' used conventional '{conventionalDefinition.FactoryId}' configuration.",
                properties: new Dictionary<string, object?>
                {
                    ["actorId"] = request.ActorId,
                    ["factoryId"] = conventionalDefinition.FactoryId,
                    ["initialAppearance"] = request.InitialAppearance,
                });
            return conventionalDefinition;
        }

        if (!IsConventionFactory(request.FallbackFactoryId))
        {
            throw new ActorNotFoundException(request.ActorId);
        }

        var resourcePathPrefix = request.FallbackFactoryId == ActorFactoryIds.Character
            ? conventions.CharactersPath
            : conventions.BackgroundsPath;
        var inferred = new ActorDefinition(
            request.ActorId,
            request.FallbackFactoryId,
            new TextureActorDefinitionData(
                definitionId,
                resourcePathPrefix,
                request.InitialAppearance,
                [],
                request.InitialPosition ?? CharacterPositionPreset.Center)
            { ResourceId = definitionId },
            definitionId);
        logger.Debug(
            "actor",
            "actor.definition_inferred",
            $"Actor '{request.ActorId}' used conventional '{request.FallbackFactoryId}' configuration.",
            properties: new Dictionary<string, object?>
            {
                ["actorId"] = request.ActorId,
                ["factoryId"] = request.FallbackFactoryId,
                ["initialAppearance"] = request.InitialAppearance,
                ["resourcePathPrefix"] = resourcePathPrefix,
            });
        return inferred;
    }

    private void ThrowIfUnsupported(string definitionId)
    {
        if (unsupportedDefinitions.TryGetValue(definitionId, out var unsupported))
            throw new ActorDefinitionException(definitionId,
                $"Factory '{unsupported.FactoryId}' does not support this actor manager environment.");
    }

    private bool TryResolveConventionDefinition(
        ActorSpawnRequest request,
        out ActorDefinition definition)
    {
        ActorDefinition? match = null;
        foreach (var factory in factories.Values)
        {
            if (request.RequiredFactoryId is not null &&
                !string.Equals(factory.FactoryId, request.RequiredFactoryId, StringComparison.Ordinal))
                continue;
            if (factory is not IConventionActorFactory convention ||
                !convention.TryResolveDefinition(request, out var candidate))
            {
                continue;
            }

            if (request.FallbackFactoryId != ActorFactoryIds.SceneObject && !string.Equals(
                    candidate.FactoryId,
                    factory.FactoryId,
                    StringComparison.Ordinal))
            {
                throw new ActorDefinitionException(
                    request.ActorId,
                    $"Convention factory '{factory.FactoryId}' inferred definition for " +
                    $"factory '{candidate.FactoryId}'.");
            }

            if (match is not null)
                throw new ActorDefinitionException(
                    request.DefinitionId ?? request.ActorId,
                    $"Convention actor definition is ambiguous between factories '{match.FactoryId}' and '{candidate.FactoryId}'. Specify a factory explicitly.");
            match = candidate;
        }

        definition = match!;
        return match is not null;
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

    private static bool IsConventionFactory(string factoryId) =>
        factoryId is ActorFactoryIds.Character or ActorFactoryIds.Background;

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
                    ReleaseActor(candidate);
                    candidate.Destroy();
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
                    if (owner.pendingActors.Count != 0)
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
                try { owner.ReleaseActor(actor); actor.Destroy(); }
                catch (Exception error) { (failures ??= new()).Add(error); }
            if (failures is not null) throw new AggregateException("Actor replacement cleanup failed.", failures);
            return ValueTask.CompletedTask;
        }
    }

    private void DestroyAll()
    {
        IActor[] snapshot;
        lock (gate)
        {
            snapshot = actorOrder.AsEnumerable().Reverse().Select(id => actors[id]).ToArray();
            actors.Clear();
            actorOrder.Clear();
        }
        var exceptions = new List<Exception>();
        foreach (var actor in snapshot)
        {
            try
            {
                ReleaseActor(actor);
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

    private void HandleDialogueLocaleChanged(LocaleChangedEvent change)
    {
        IActor[] snapshot;
        lock (gate)
        {
            snapshot = actors.Values.Where(actor => actor.Visible).ToArray();
        }

        foreach (var actor in snapshot)
        {
            try
            {
                actor.SetAppearance(actor.Appearance);
            }
            catch (Exception exception)
            {
                logger.Warning(
                    "locale",
                    "locale.actor_refresh_failed",
                    $"Actor '{actor.Id}' kept its previous appearance after dialogue locale switched to '{change.CurrentLocale}': {exception.Message}",
                    exception);
            }
        }
    }

    // Called under gate so both public entry points publish the same creation metadata.
    private Task<IActor> StartCreation(
        string actorId,
        ActorDefinition definition,
        IActorFactory factory,
        ActorCreationMode creationMode)
    {
        factory.ValidateDefinition(definition);
        var completion = new TaskCompletionSource<IActor>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pendingActors.Add(actorId, new PendingActorCreation(
            completion.Task, definition.FactoryId, definition.DefinitionId));
        _ = CompleteCreationAsync(
            actorId, definition, factory, creationMode, completion, actorLifetime.Token);
        return completion.Task;
    }

    private sealed record PendingActorCreation(Task<IActor> Task, string FactoryId, string DefinitionId);

    private async Task CompleteCreationAsync(
        string actorId,
        ActorDefinition definition,
        IActorFactory factory,
        ActorCreationMode creationMode,
        TaskCompletionSource<IActor> completion,
        CancellationToken cancellationToken)
    {
        IActor? actor = null;
        var claimed = false;
        try
        {
            actor = await factory.CreateAsync(definition, creationMode, cancellationToken);
            ClaimCreatedActor(actor);
            claimed = true;
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCreatedActor(actorId, definition, factory, actor);
            BindActor(actor);
            await MutateAsync(_ =>
            {
                lock (gate)
                {
                    actors.Add(actorId, actor);
                    actorOrder.Add(actorId);
                }
                return Task.CompletedTask;
            }, cancellationToken);

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
            try
            {
                if (actor is not null && claimed)
                {
                    ReleaseActor(actor);
                    actor.Destroy();
                }
            }
            finally
            {
                lock (gate) pendingActors.Remove(actorId);
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
            pending = pendingActors.Values.Select(creation => creation.Task).ToArray();
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

    private void ClaimCreatedActor(IActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (ActorOwnerGate)
        {
            if (ActorOwners.TryGetValue(actor, out _))
                throw new ActorException($"Actor '{actor.Id}' is already managed. Factories must return a new instance.");
            ActorOwners.Add(actor, this);
        }
    }

    private void BindActor(IActor actor)
    {
        if (actor is not IStateMutationParticipant participant)
            throw new StateCompatibilityException(
                $"Actor '{actor.Id}' does not implement the persistent write gate contract.");
        if (mutationGate is not null) participant.BindStateMutationGate(mutationGate);
    }

    private void ReleaseActor(IActor actor)
    {
        lock (ActorOwnerGate)
            if (ActorOwners.TryGetValue(actor, out var owner) && ReferenceEquals(owner, this))
                ActorOwners.Remove(actor);
    }

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

    public void Dispose() => actorLifetime.Dispose();
}
