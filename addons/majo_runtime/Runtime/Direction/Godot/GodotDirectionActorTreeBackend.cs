#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Operations;
using Majo.Runtime.Timing;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Direction.Godot;

/// <summary>
/// Prepares a complete actor tree below a hidden root. The active tree is never
/// mutated during Prepare; Commit only swaps root visibility and ownership.
/// </summary>
internal sealed class GodotDirectionActorTreeBackend
{
    private long nextTreeId;
    private readonly GodotDirectionRenderHost host;
    private readonly Node2D actorRoot;
    private readonly Func<Node2D, IActorManager> createManager;
    private readonly IEasingRegistry easings;
    private readonly ITransitionDriverRegistry drivers;
    private PreparedActorTreeState? active;

    public GodotDirectionActorTreeBackend(
        GodotDirectionRenderHost host,
        Node2D actorRoot,
        Func<Node2D, IActorManager> createManager,
        IEasingRegistry? easings = null, ITransitionDriverRegistry? drivers = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.actorRoot = actorRoot ?? throw new ArgumentNullException(nameof(actorRoot));
        this.createManager = createManager ?? throw new ArgumentNullException(nameof(createManager));
        this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
        this.drivers = drivers ?? new TransitionDriverRegistry();
    }



    public async Task<IPreparedDirectionComponent> PrepareAsync(
        DirectionPlan plan,
        StageState? before,
        StageState? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var transitions = ResolveTransitions(plan, before, target);
        var root = new Node2D
        {
            Name = $"PreparedActors-{Interlocked.Increment(ref nextTreeId)}",
            Visible = false,
        };
        actorRoot.AddChild(root);

        IActorManager? manager = null;
        try
        {
            manager = createManager(root) ?? throw new DirectionException(
                "Direction actor manager factory returned null.");
            await manager.InitializeAsync(cancellationToken);

            if (target is not null)
            {
                foreach (var actorState in target.Actors.Values
                    .OrderBy(actor => actor.Id.Value, StringComparer.Ordinal))
                {
                    ActorState? sourceActorState = null;
                    before?.Actors.TryGetValue(actorState.Id, out sourceActorState);
                    await PrepareActorAsync(
                        manager,
                        actorState,
                        sourceActorState,
                        cancellationToken);
                }
            }

            var prepared = new PreparedActorTreeState(root, manager);
            ConfigurePreparedActors(prepared, before, target, transitions);
            return new PreparedActorTree(
                this,
                prepared,
                before,
                target,
                transitions);
        }
        catch (Exception preparationException)
        {
            List<Exception>? failures = null;
            if (manager is not null)
            {
                try { await manager.ShutdownAsync(CancellationToken.None); }
                catch (Exception exception) { failures = new List<Exception> { exception }; }
            }
            try { ReleaseRoot(root); }
            catch (Exception exception) { (failures ??= new()).Add(exception); }
            if (failures is not null)
                throw new AggregateException(
                    "Direction actor preparation failed and cleanup also failed.",
                    new[] { preparationException }.Concat(failures));
            throw;
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = active;
        active = null;
        if (previous is not null) await previous.DisposeAsync(cancellationToken);
    }

    private static async Task PrepareActorAsync(
        IActorManager manager,
        ActorState target,
        ActorState? source,
        CancellationToken cancellationToken)
    {
        target.Validate();
        var initialAppearance = source?.Appearance ?? target.Appearance ?? "Default";
        var actor = await manager.GetOrCreateAsync(
            new ActorSpawnRequest(
                target.Id.Value,
                ActorFactoryIds.Character,
                initialAppearance,
                creationMode: ActorCreationMode.Dormant),
            cancellationToken);
        if (actor is not ICharacterActor character)
        {
            throw new DirectionException(
                $"Direction Actor '{target.Id.Value}' must use a character actor factory.");
        }

        if (source?.Appearance is { } sourceAppearance &&
            !string.Equals(sourceAppearance, target.Appearance, StringComparison.Ordinal))
        {
            // Factories initialize from their manifest/definition default. A
            // replacement tree must start at the currently visible
            // appearance so ChangeAppearance can transition from the real
            // source expression instead of showing Default for one frame.
            character.SetAppearance(sourceAppearance);
        }

        if (target.Appearance is not null)
        {
            await character.PrepareAppearanceAsync(target.Appearance, cancellationToken);
            if (source is null ||
                string.Equals(source.Appearance, target.Appearance, StringComparison.Ordinal))
            {
                character.SetAppearance(target.Appearance);
            }
        }

        character.SetScenePosition(new Vector2(target.Position.X, target.Position.Y));
        character.SetScale(new Vector2(target.Scale, target.Scale));
        character.SetZIndex(target.Z);
        character.SetOpacity(1);
        character.SetVisible(true);
    }

    private Dictionary<ActorId, DirectionTransitionSpec> ResolveTransitions(
        DirectionPlan plan,
        StageState? before,
        StageState? target)
    {
        var beforeActors = before?.Actors ?? DirectionCollections.Empty<ActorId, ActorState>();
        var targetActors = target?.Actors ?? DirectionCollections.Empty<ActorId, ActorState>();
        var transitions = new Dictionary<ActorId, DirectionTransitionSpec>();
        foreach (var actorId in beforeActors.Keys
            .Union(targetActors.Keys)
            .OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            beforeActors.TryGetValue(actorId, out var source);
            targetActors.TryGetValue(actorId, out var destination);
            if (Equals(source, destination)) continue;

            var prefix = $"actor/{before?.Id.Value ?? target?.Id.Value}/{actorId.Value}/";
            var specs = plan.Transitions
                .Where(transition => transition.StablePaths.Any(path =>
                    path == "*" || path.StartsWith(prefix, StringComparison.Ordinal)))
                .Select(transition => transition.Spec)
                .Distinct()
                .ToArray();
            var transition = specs.Length switch
            {
                0 => DirectionTransitionSpec.Cut,
                1 => specs[0],
                _ => throw new DirectionException(
                    $"Direction Actor '{actorId.Value}' has multiple transition specifications."),
            };
            ValidateTransition(actorId, source, destination, transition);
            transitions.Add(actorId, transition);
        }

        return transitions;
    }

    private void ValidateTransition(
        ActorId actorId,
        ActorState? before,
        ActorState? target,
        DirectionTransitionSpec transition)
    {
        transition.Validate();
        if (transition.Kind != DirectionTransitionId.Move && !drivers.Supports(transition.Kind, TransitionDomains.Actor))
            throw new DirectionException($"Transition '{transition.Kind}' does not support actors.");
        if (!easings.Contains(transition.Easing))
        {
            throw new DirectionException(
                $"Direction Actor '{actorId.Value}' does not support easing '{transition.Easing}'.");
        }


        if (before is not null && target is not null &&
            !string.Equals(before.Appearance, target.Appearance, StringComparison.Ordinal) &&
            transition.Kind == DirectionTransitionId.Fade)
        {
            throw new DirectionException(
                $"Direction Actor '{actorId.Value}' appearance change from '{before.Appearance}' to '{target.Appearance}' does not support fade; use dissolve instead.");
        }


        if (transition.Kind == DirectionTransitionId.Move &&
            (before is null || target is null))
        {
            throw new DirectionException(
                $"Direction Actor '{actorId.Value}' can only move when it exists in both " +
                "the source and target states; use cut, fade, or dissolve for entrance and leave.");
        }
    }



    private static void ConfigurePreparedActors(
        PreparedActorTreeState prepared,
        StageState? before,
        StageState? target,
        IReadOnlyDictionary<ActorId, DirectionTransitionSpec> transitions)
    {
        if (target is null) return;
        foreach (var pair in transitions)
        {
            if (!target.Actors.TryGetValue(pair.Key, out var targetState) ||
                !prepared.TryGetCharacter(pair.Key, out var actor) ||
                pair.Value.DurationSeconds <= 0)
            {
                continue;
            }

            if (IsAppearanceChange(before, target, pair.Key) &&
                pair.Value.Kind != DirectionTransitionId.Move)
            {
                continue;
            }

            if (pair.Value.Kind != DirectionTransitionId.Move &&
                pair.Value.Kind != DirectionTransitionId.Cut)
            {
                actor!.SetOpacity(0);
            }
            else if (pair.Value.Kind == DirectionTransitionId.Move &&
                before?.Actors.TryGetValue(pair.Key, out var sourceState) == true)
            {
                actor!.SetScenePosition(new Vector2(
                    sourceState.Position.X,
                    sourceState.Position.Y));
            }
        }
    }

    private static bool IsAppearanceChange(
        StageState? before,
        StageState? target,
        ActorId actorId) =>
        before?.Actors.TryGetValue(actorId, out var source) == true &&
        target?.Actors.TryGetValue(actorId, out var destination) == true &&
        !string.Equals(source.Appearance, destination.Appearance, StringComparison.Ordinal);

    private PreparedActorTreeState? Publish(PreparedActorTreeState target)
    {
        var previous = active;
        host.ActivateOutput();
        target.Root.Visible = true;
        active = target;
        return previous;
    }

    private void Restore(PreparedActorTreeState? previous, PreparedActorTreeState target)
    {
        if (ReferenceEquals(active, target))
            active = previous;
        target.Root.Visible = false;
        if (previous is not null && GodotObject.IsInstanceValid(previous.Root))
            previous.Root.Visible = true;
    }

    private static void ReleaseRoot(Node2D root)
    {
        if (!GodotObject.IsInstanceValid(root)) return;
        root.GetParent()?.RemoveChild(root);
        root.QueueFree();
    }

    private sealed class PreparedActorTree : IPreparedDirectionComponent
    {
        private readonly GodotDirectionActorTreeBackend backend;
        private readonly PreparedActorTreeState target;
        private readonly StageState? sourceState;
        private readonly StageState? targetState;
        private readonly IReadOnlyDictionary<ActorId, DirectionTransitionSpec> transitions;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IOperationHandle> operations = new();
        private PreparedActorTreeState? replaced;
        private bool committed;
        private bool rolledBack;
        private bool disposed;

        public PreparedActorTree(
            GodotDirectionActorTreeBackend backend,
            PreparedActorTreeState target,
            StageState? sourceState,
            StageState? targetState,
            IReadOnlyDictionary<ActorId, DirectionTransitionSpec> transitions)
        {
            this.backend = backend;
            this.target = target;
            this.sourceState = sourceState;
            this.targetState = targetState;
            this.transitions = transitions;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed || rolledBack || disposed)
                throw new InvalidOperationException("Actor tree is no longer preparable.");
            committed = true;
            replaced = backend.Publish(target);
            StartTransitions();
            if (operations.Count == 0)
            {
                CompleteSettlement();
            }
            else
            {
                _ = ObserveOperationsAsync();
            }
        }

        public async ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committed || rolledBack || disposed)
                throw new InvalidOperationException("Actor tree cannot finish in its current state.");
            foreach (var operation in operations) operation.Finish();
            await Task.WhenAll(operations.Select(operation => operation.Completion))
                .WaitAsync(cancellationToken);
            CompleteSettlement();
        }

        public void Rollback(DirectionCommitToken token)
        {
            if (rolledBack || disposed) return;
            rolledBack = true;
            List<Exception>? failures = null;
            foreach (var operation in operations)
            {
                try { operation.Cancel(); }
                catch (Exception exception) { (failures ??= new()).Add(exception); }
            }
            try { backend.Restore(replaced, target); }
            catch (Exception exception) { (failures ??= new()).Add(exception); }
            settlement.TrySetCanceled();
            committed = false;
            if (failures is not null)
                throw new AggregateException(
                    "Actor tree rollback failed to fully cancel and restore.", failures);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            if (!settlement.Task.IsCompleted)
                settlement.TrySetCanceled();
            if (!committed)
            {
                await target.DisposeAsync(CancellationToken.None);
                return;
            }

            if (replaced is not null)
                await replaced.DisposeAsync(CancellationToken.None);
        }

        private void StartTransitions()
        {
            HideNonTransitioningSourceActors();
            foreach (var pair in transitions)
            {
                ICharacterActor? source = null;
                var hasSource = replaced is not null &&
                    replaced.TryGetCharacter(pair.Key, out source);
                var hasTarget = target.TryGetCharacter(pair.Key, out var destination);
                if (hasSource && hasTarget && IsAppearanceChange(pair.Key))
                {
                    var targetAppearance = targetState!.Actors[pair.Key].Appearance;
                    if (targetAppearance is not null)
                    {
                        if (pair.Value.DurationSeconds > 0)
                        {
                            operations.Add(destination!.ChangeAppearance(
                                targetAppearance,
                                new AnimationSpec(pair.Value.DurationSeconds, pair.Value.Easing),
                                backend.drivers.GetRequired(pair.Value.Kind == DirectionTransitionId.Move
                                    ? DirectionTransitionId.Dissolve : pair.Value.Kind)));
                        }
                        else
                        {
                            destination!.SetAppearance(targetAppearance);
                        }
                    }

                    if (pair.Value.Kind == DirectionTransitionId.Move &&
                        pair.Value.DurationSeconds > 0)
                    {
                        var state = targetState!.Actors[pair.Key];
                        destination!.SetScenePosition(new Vector2(
                            state.Position.X,
                            state.Position.Y));
                        var targetPixelPosition = destination.Position;
                        var sourcePosition = source!.Position;
                        destination.SetPosition(sourcePosition);
                        operations.Add(destination.ChangePosition(
                            targetPixelPosition,
                            new AnimationSpec(pair.Value.DurationSeconds, pair.Value.Easing)));
                    }
                    continue;
                }

                if (pair.Value.DurationSeconds <= 0) continue;
                var animation = new AnimationSpec(pair.Value.DurationSeconds, pair.Value.Easing);
                if (pair.Value.Kind == DirectionTransitionId.Fade)
                {
                    if (hasSource) operations.Add(source!.ChangeOpacity(0, animation));
                    if (hasTarget) operations.Add(destination!.ChangeOpacity(1, animation));
                }
                else if (pair.Value.Kind != DirectionTransitionId.Move && pair.Value.Kind != DirectionTransitionId.Cut)
                {
                    var actor = (hasTarget ? destination : source) as ActorBase
                        ?? throw new DirectionException("Actor factory does not provide visual transition inputs.");
                    operations.Add(actor.TransitionVisual(hasSource ? source : null, hasTarget ? destination : null,
                        animation, backend.drivers.GetRequired(pair.Value.Kind)));
                }
                else if (pair.Value.Kind == DirectionTransitionId.Move && hasTarget)
                {
                    var state = targetState!.Actors[pair.Key];
                    destination!.SetScenePosition(new Vector2(
                        state.Position.X,
                        state.Position.Y));
                    var targetPixelPosition = destination.Position;
                    var sourceState = replaced is null
                        ? throw new InvalidOperationException("Move transition has no source tree.")
                        : source!.Position;
                    destination.SetPosition(sourceState);
                    operations.Add(destination.ChangePosition(targetPixelPosition, animation));
                }
            }
        }

        private bool IsAppearanceChange(ActorId actorId) =>
            GodotDirectionActorTreeBackend.IsAppearanceChange(
                sourceState,
                targetState,
                actorId);

        private void HideNonTransitioningSourceActors()
        {
            if (replaced is null) return;
            var retainsVisibleSource = false;
            foreach (var actor in replaced.Manager.Actors)
            {
                var actorId = new ActorId(actor.Id);
                var keepForDissolve = !IsAppearanceChange(actorId) &&
                    transitions.TryGetValue(actorId, out var transition) &&
                    transition.Kind != DirectionTransitionId.Move &&
                    transition.Kind != DirectionTransitionId.Cut &&
                    transition.DurationSeconds > 0;
                if (keepForDissolve)
                    retainsVisibleSource = true;
                else
                    actor.SetVisible(false);
            }

            if (!retainsVisibleSource) replaced.Root.Visible = false;
        }

        private async Task ObserveOperationsAsync()
        {
            try
            {
                await Task.WhenAll(operations.Select(operation => operation.Completion));
                CompleteSettlement();
            }
            catch (Exception exception)
            {
                settlement.TrySetException(exception);
            }
        }

        private void CompleteSettlement()
        {
            if (settlement.Task.IsCompleted || rolledBack || disposed) return;
            if (replaced is not null) replaced.Root.Visible = false;
            settlement.TrySetResult();
        }
    }


    private sealed class PreparedActorTreeState
    {
        private bool disposed;

        public PreparedActorTreeState(Node2D root, IActorManager manager)
        {
            Root = root;
            Manager = manager;
        }

        public Node2D Root { get; }
        public IActorManager Manager { get; }

        public bool TryGetCharacter(ActorId actorId, out ICharacterActor? actor)
        {
            if (Manager.TryGet(actorId.Value, out var found) && found is ICharacterActor character)
            {
                actor = character;
                return true;
            }

            actor = null;
            return false;
        }

        public async ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            if (disposed) return;
            disposed = true;
            Exception? failure = null;
            try { await Manager.ShutdownAsync(cancellationToken); }
            catch (Exception exception) { failure = exception; }
            try { ReleaseRoot(Root); }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }
            if (failure is not null) throw failure;
        }
    }
}
