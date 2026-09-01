#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Operations;

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
    private PreparedActorTreeState? active;

    public GodotDirectionActorTreeBackend(
        GodotDirectionRenderHost host,
        Node2D actorRoot,
        Func<Node2D, IActorManager> createManager)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.actorRoot = actorRoot ?? throw new ArgumentNullException(nameof(actorRoot));
        this.createManager = createManager ?? throw new ArgumentNullException(nameof(createManager));
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
                    await PrepareActorAsync(manager, actorState, cancellationToken);
                }
            }

            var prepared = new PreparedActorTreeState(root, manager);
            ConfigurePreparedActors(prepared, before, target, transitions);
            return new PreparedActorTree(
                this,
                prepared,
                target,
                transitions);
        }
        catch
        {
            if (manager is not null)
                await manager.ShutdownAsync(CancellationToken.None);
            ReleaseRoot(root);
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
        CancellationToken cancellationToken)
    {
        target.Validate();
        var actor = await manager.GetOrCreateAsync(
            new ActorSpawnRequest(
                target.Id.Value,
                ActorFactoryIds.Character,
                target.Appearance ?? "Default",
                creationMode: ActorCreationMode.Dormant),
            cancellationToken);
        if (actor is not ICharacterActor character)
        {
            throw new DirectionException(
                $"Direction Actor '{target.Id.Value}' must use a character actor factory.");
        }

        if (target.Appearance is not null)
        {
            await character.PrepareAppearanceAsync(target.Appearance, cancellationToken);
            character.SetAppearance(target.Appearance);
        }

        character.SetScenePosition(new Vector2(target.Position.X, target.Position.Y));
        character.SetScale(new Vector2(target.Scale, target.Scale));
        character.SetZIndex(target.Z);
        character.SetOpacity(1);
        character.SetVisible(true);
    }

    private static IReadOnlyDictionary<ActorId, DirectionTransitionSpec> ResolveTransitions(
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

    private static void ValidateTransition(
        ActorId actorId,
        ActorState? before,
        ActorState? target,
        DirectionTransitionSpec transition)
    {
        transition.Validate();
        if (transition.Kind == DirectionTransitionKind.Dissolve)
        {
            throw new DirectionException(
                $"Direction Actor '{actorId.Value}' does not support dissolve; use fade.");
        }

        if (transition.Kind == DirectionTransitionKind.Move &&
            (before is null || target is null))
        {
            throw new DirectionException(
                $"Direction Actor '{actorId.Value}' can only move when it exists in both " +
                "the source and target states; use cut or fade for entrance and leave.");
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

            if (pair.Value.Kind == DirectionTransitionKind.Fade)
            {
                actor!.SetOpacity(0);
            }
            else if (pair.Value.Kind == DirectionTransitionKind.Move &&
                before?.Actors.TryGetValue(pair.Key, out var sourceState) == true)
            {
                actor!.SetScenePosition(new Vector2(
                    sourceState.Position.X,
                    sourceState.Position.Y));
            }
        }
    }

    private PreparedActorTreeState? Publish(PreparedActorTreeState target)
    {
        var previous = active;
        host.ActivateOutput();
        target.Root.Visible = true;
        active = target;
        return previous;
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
        private readonly StageState? targetState;
        private readonly IReadOnlyDictionary<ActorId, DirectionTransitionSpec> transitions;
        private readonly TaskCompletionSource settlement = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IOperationHandle> operations = new();
        private PreparedActorTreeState? replaced;
        private bool committed;
        private bool disposed;

        public PreparedActorTree(
            GodotDirectionActorTreeBackend backend,
            PreparedActorTreeState target,
            StageState? targetState,
            IReadOnlyDictionary<ActorId, DirectionTransitionSpec> transitions)
        {
            this.backend = backend;
            this.target = target;
            this.targetState = targetState;
            this.transitions = transitions;
        }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            if (committed) throw new InvalidOperationException("Actor tree already committed.");
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
            if (!committed) throw new InvalidOperationException("Actor tree has not committed.");
            foreach (var operation in operations) operation.Complete();
            await Task.WhenAll(operations.Select(operation => operation.Completion))
                .WaitAsync(cancellationToken);
            CompleteSettlement();
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            if (!committed)
            {
                settlement.TrySetCanceled();
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
                if (pair.Value.DurationSeconds <= 0) continue;
                var animation = ToAnimationSpec(pair.Value);
                ICharacterActor? source = null;
                var hasSource = replaced is not null &&
                    replaced.TryGetCharacter(pair.Key, out source);
                var hasTarget = target.TryGetCharacter(pair.Key, out var destination);
                if (pair.Value.Kind == DirectionTransitionKind.Fade)
                {
                    if (hasSource) operations.Add(source!.ChangeOpacity(0, animation));
                    if (hasTarget) operations.Add(destination!.ChangeOpacity(1, animation));
                }
                else if (pair.Value.Kind == DirectionTransitionKind.Move && hasTarget)
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

        private void HideNonTransitioningSourceActors()
        {
            if (replaced is null) return;
            var retainsVisibleSource = false;
            foreach (var actor in replaced.Manager.Actors)
            {
                var actorId = new ActorId(actor.Id);
                var keepForFade = transitions.TryGetValue(actorId, out var transition) &&
                    transition.Kind == DirectionTransitionKind.Fade &&
                    transition.DurationSeconds > 0;
                if (keepForFade)
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
            if (settlement.Task.IsCompleted) return;
            if (replaced is not null) replaced.Root.Visible = false;
            settlement.TrySetResult();
        }

        private static AnimationSpec ToAnimationSpec(DirectionTransitionSpec transition)
        {
            var (curve, ease) = transition.Ease switch
            {
                DirectionEase.Linear => (AnimationTransition.Linear, AnimationEase.InOut),
                DirectionEase.SineIn => (AnimationTransition.Sine, AnimationEase.In),
                DirectionEase.SineOut => (AnimationTransition.Sine, AnimationEase.Out),
                DirectionEase.SineInOut => (AnimationTransition.Sine, AnimationEase.InOut),
                DirectionEase.CubicIn => (AnimationTransition.Cubic, AnimationEase.In),
                DirectionEase.CubicOut => (AnimationTransition.Cubic, AnimationEase.Out),
                DirectionEase.CubicInOut => (AnimationTransition.Cubic, AnimationEase.InOut),
                _ => throw new ArgumentOutOfRangeException(nameof(transition)),
            };
            return new AnimationSpec(transition.DurationSeconds, curve, ease);
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
            await Manager.ShutdownAsync(cancellationToken);
            ReleaseRoot(Root);
        }
    }
}
