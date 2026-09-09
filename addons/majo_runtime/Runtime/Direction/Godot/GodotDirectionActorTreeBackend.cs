#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Direction.Godot;

/// <summary>Stable per-instance bindings. Prepare stages only changed resources; Commit publishes slot writers.</summary>
internal sealed class GodotDirectionActorTreeBackend
{
    private readonly GodotDirectionRenderHost host;
    private readonly Node2D actorRoot;
    private readonly Func<Node2D, IActorManager> createManager;
    private readonly IEasingRegistry easings;
    private readonly ITransitionDriverRegistry drivers;
    private readonly Dictionary<ActorId, Binding> active = new();
    private long generation;

    public GodotDirectionActorTreeBackend(GodotDirectionRenderHost host, Node2D actorRoot,
        Func<Node2D, IActorManager> createManager, IEasingRegistry? easings = null,
        ITransitionDriverRegistry? drivers = null)
    {
        this.host = host;
        this.actorRoot = actorRoot;
        this.createManager = createManager;
        this.easings = easings ?? BuiltInEasings.CreateDefaultRegistry();
        this.drivers = drivers ?? new TransitionDriverRegistry();
    }

    public async Task<IPreparedDirectionComponent> PrepareAsync(DirectionPlan plan, StageState? before,
        StageState? target, CancellationToken cancellationToken)
    {
        foreach (var transition in plan.Transitions.Where(value => value.StablePaths.Any(path =>
            path.StartsWith($"actor/{(target ?? before)!.Id.Value}/", StringComparison.Ordinal))))
            try { easings.GetRequired(transition.Spec.Easing); }
            catch (KeyNotFoundException error) { throw new DirectionException(error.Message); }
        var prepared = new Dictionary<ActorId, Binding>();
        var visuals = new Dictionary<ActorId, IPreparedActorAppearance>();
        var retentions = new List<IDisposable>();
        try
        {
            if (target is not null)
                foreach (var state in target.Actors.Values)
                {
                    if (!state.Visible && !active.ContainsKey(state.Id)) continue;
                    if (!active.TryGetValue(state.Id, out var binding))
                    {
                        binding = await CreateBindingAsync(state, before?.Actors.ContainsKey(state.Id) == true || plan.ActorIntents.Count == 0, cancellationToken);
                        prepared.Add(state.Id, binding);
                    }
                    if (binding.Appearance.RetainVisualPreparation() is { } retention) retentions.Add(retention);
                    if (state.Appearance is { } appearance && binding.Actor.Appearance != appearance)
                        await binding.Appearance.PrepareAppearanceAsync(appearance, cancellationToken);
                }
            foreach (var patch in plan.ActorIntents.OfType<ActorPatch>())
                if (patch.StageId == target?.Id && patch.Appearance.HasValue &&
                    before?.Actors.TryGetValue(patch.ActorId, out var original) == true && original.Visible &&
                    patch.Transition.Kind != DirectionTransitionId.Cut && patch.Transition.DurationSeconds > 0)
                {
                    var binding = active[patch.ActorId];
                    visuals.Add(patch.ActorId, await binding.Appearance.PrepareAppearanceTransitionAsync(
                        patch.Appearance.Value ?? "Default",
                        new AnimationSpec(patch.Transition.DurationSeconds, patch.Transition.Easing),
                        drivers.GetRequired(patch.Transition.Kind), cancellationToken));
                }
            return new PreparedUpdate(this, plan, before, target, prepared, visuals, retentions);
        }
        catch
        {
            foreach (var visual in visuals.Values) visual.Dispose();
            foreach (var retention in retentions) retention.Dispose();
            foreach (var binding in prepared.Values) await binding.DisposeAsync();
            throw;
        }
    }

    private async Task<Binding> CreateBindingAsync(ActorState state, bool restore, CancellationToken token)
    {
        var root = new Node2D { Name = $"Actor-{state.Id.Value}-{++generation}", Visible = false };
        actorRoot.AddChild(root);
        IActorManager? manager = null;
        try
        {
            manager = createManager(root);
            await manager.InitializeAsync(token);
            var actor = await manager.GetOrCreateAsync(new ActorSpawnRequest(state.Id.Value,
                state.Kind == DirectionActorKind.SceneObject ? ActorFactoryIds.SceneObject : ActorFactoryIds.Character,
                state.Appearance ?? "Default", creationMode: ActorCreationMode.Dormant)
            { DefinitionId = state.DefinitionId }, token);
            var binding = new Binding(host, new ActorFxTarget(default, ActorFxSlot.Instance, state.Id), root, manager, actor);
            if (restore) binding.Apply(state);
            return binding;
        }
        catch
        {
            try { if (manager is not null) await manager.ShutdownAsync(CancellationToken.None); }
            finally { Release(root); }
            throw;
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var bindings = active.Values.ToArray();
        active.Clear();
        List<Exception>? failures = null;
        foreach (var binding in bindings)
        {
            try { await binding.DisposeAsync(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Actor binding cleanup failed.", failures);
    }

    private static void Release(Node root)
    {
        if (!GodotObject.IsInstanceValid(root)) return;
        root.GetParent()?.RemoveChild(root);
        root.QueueFree();
    }

    private DirectionSlotAction Animate(DirectionAnimationSlot slot, DirectionTransitionSpec transition,
        Action<float> apply, Action? complete = null)
    {
        var action = slot.Replace(apply, complete);
        if (transition.DurationSeconds == 0) action.Finish();
        else action.Attach(host.Animate(transition, easings.GetRequired(transition.Easing),
            action.Apply, action.Finish, action.Fail));
        return action;
    }

    private sealed class Binding
    {
        private readonly GodotDirectionRenderHost host;
        public ActorFxTarget FxTarget;
        public readonly GodotActorModifierTarget Modifier;
        public readonly Node2D Root;
        public readonly IActorManager Manager;
        public readonly IActor Actor;
        public readonly IActorTransformTarget Transform;
        public readonly IActorAppearanceTarget Appearance;
        public readonly IActorVisibilityTarget Visibility;
        public readonly DirectionAnimationSlot Space = new();
        public readonly DirectionAnimationSlot Presence = new();
        public readonly DirectionAnimationSlot AppearanceSlot = new();
        public Task ReleaseTask = Task.CompletedTask;
        public long HideGeneration;
        private bool disposed;

        public Binding(GodotDirectionRenderHost host, ActorFxTarget fxTarget, Node2D root, IActorManager manager, IActor actor)
        {
            this.host = host; FxTarget = fxTarget;
            Root = root; Manager = manager; Actor = actor;
            Modifier = new GodotActorModifierTarget(root, () => actor.Position);
            Transform = actor as IActorTransformTarget ?? throw new DirectionException($"Actor '{actor.Id}' lacks transform capability.");
            Appearance = actor as IActorAppearanceTarget ?? throw new DirectionException($"Actor '{actor.Id}' lacks appearance capability.");
            Visibility = actor as IActorVisibilityTarget ?? throw new DirectionException($"Actor '{actor.Id}' lacks visibility capability.");
        }

        public ActorState CurrentPose(ActorState state) => state with
        {
            Position = new DirectionPoint(Transform.ScenePosition.X, Transform.ScenePosition.Y),
            Scale = Transform.Scale.X,
            RotationDegrees = Transform.RotationDegrees,
        };

        public void ApplySpace(ActorState state)
        {
            Transform.SetScenePosition(new Vector2(state.Position.X, state.Position.Y));
            Transform.SetScale(new Vector2(state.Scale, state.Scale));
            Transform.SetRotationDegrees(state.RotationDegrees);
            Modifier.Refresh();
        }

        public void Apply(ActorState state)
        {
            ApplySpace(state);
            if (state.Appearance is { } appearance) Appearance.SetAppearance(appearance);
            Actor.SetZIndex(state.Z);
            Visibility.SetOpacity(state.Visible ? 1 : 0);
            Visibility.SetVisible(state.Visible);
        }

        public async Task DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            Space.Finish();
            Presence.Cancel();
            AppearanceSlot.Finish();
            Exception? failure = null;
            try { await host.ReleaseActorModifierTargetAsync(FxTarget, Modifier); }
            catch (Exception error) { failure = error; }
            try { await Manager.ShutdownAsync(CancellationToken.None); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            finally { Release(Root); }
            if (failure is not null) throw failure;
        }
    }

    private sealed class PreparedUpdate : IPreparedDirectionComponent
    {
        private readonly GodotDirectionActorTreeBackend backend;
        private readonly DirectionPlan plan;
        private readonly StageState? before;
        private readonly StageState? target;
        private readonly Dictionary<ActorId, Binding> added;
        private readonly Dictionary<ActorId, Binding> removed = new();
        private readonly Dictionary<ActorId, ActorState> previous = new();
        private readonly List<DirectionSlotAction> slots = new();
        private readonly Dictionary<ActorId, IPreparedActorAppearance> visuals;
        private readonly List<IDisposable> retentions;
        private readonly List<Task> waits = new();
        private readonly TaskCompletionSource<bool> confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool committed;
        private bool rolledBack;
        private bool disposed;

        public PreparedUpdate(GodotDirectionActorTreeBackend backend, DirectionPlan plan,
            StageState? before, StageState? target, Dictionary<ActorId, Binding> added,
            Dictionary<ActorId, IPreparedActorAppearance> visuals, List<IDisposable> retentions)
        { this.retentions = retentions; this.backend = backend; this.plan = plan; this.before = before; this.target = target; this.added = added; this.visuals = visuals; }

        public Task Settlement => settlement.Task;

        public void Commit(DirectionCommitToken token)
        {
            committed = true;
            backend.host.ActivateOutput();
            foreach (var (id, binding) in backend.active)
                if (before?.Actors.TryGetValue(id, out var state) == true)
                    previous[id] = binding.CurrentPose(state);
            foreach (var (id, binding) in added)
            {
                backend.active.Add(id, binding);
                binding.FxTarget = new ActorFxTarget(target!.Id, ActorFxSlot.Instance, id);
                backend.host.RegisterActorModifierTarget(binding.FxTarget, binding.Modifier);
                binding.Root.Visible = true;
                if (before?.Actors.ContainsKey(id) != true && target is not null)
                    plan.CommitActors[(target.Id, id)] = binding.CurrentPose(target.Actors[id]) with
                    { Appearance = target.Actors[id].Appearance ?? binding.Actor.Appearance };
            }
            foreach (var (id, binding) in backend.active.ToArray())
                if (target?.Actors.ContainsKey(id) != true ||
                    plan.ActorIntents.Count == 0 && target.Actors.TryGetValue(id, out var restored) && !restored.Visible)
                {
                    binding.Space.Finish();
                    binding.Presence.Cancel();
                    binding.AppearanceSlot.Finish();
                    binding.Root.Visible = false;
                    removed.Add(id, binding);
                    backend.active.Remove(id);
                }

            var intents = plan.ActorIntents.Where(MatchesStage).ToArray();
            if (intents.Length == 0)
            {
                // Stable-state restore has no authored intents.
                if (target is not null)
                    foreach (var (id, state) in target.Actors)
                        if (backend.active.TryGetValue(id, out var binding))
                        {
                            binding.Space.Finish(); binding.Presence.Cancel(); binding.AppearanceSlot.Finish();
                            binding.Apply(state);
                        }
            }
            else
            {
                // Visibility cleanup runs after every slot has been installed, including duration-zero hide.
                foreach (var intent in intents.Where(value => value is not ActorHidePatch)) ApplyIntent(intent);
                foreach (var intent in intents.OfType<ActorHidePatch>()) ApplyHide(intent);
            }
            _ = ObserveAsync();
        }

        private bool MatchesStage(IDurableDirectionPatch patch) => patch switch
        {
            ActorPatch value => value.StageId == target?.Id,
            ActorMovePatch value => value.StageId == target?.Id,
            ActorHidePatch value => value.StageId == target?.Id,
            _ => false,
        };

        private void ApplyIntent(IDurableDirectionPatch intent)
        {
            var id = intent is ActorPatch statePatch ? statePatch.ActorId : ((ActorMovePatch)intent).ActorId;
            var state = target!.Actors[id];
            var binding = backend.active[id];
            ActorState? original = null;
            before?.Actors.TryGetValue(id, out original);
            if (intent is ActorPatch patch)
            {
                var showing = original is null || !original.Visible;
                if (patch.Appearance.HasValue)
                {
                    binding.AppearanceSlot.Finish();
                    if (showing || patch.Transition.Kind == DirectionTransitionId.Cut || patch.Transition.DurationSeconds == 0)
                        binding.Appearance.SetAppearance(state.Appearance ?? "Default");
                    else
                    {
                        var visual = visuals[id];
                        visual.Commit();
                        var action = backend.Animate(binding.AppearanceSlot, patch.Transition,
                            visual.ApplyProgress, visual.Finish);
                        slots.Add(action);
                    }
                }
                if (patch.Position.HasValue || patch.Scale.HasValue || patch.RotationDegrees.HasValue)
                    ApplySpace(binding, state, patch.Position, patch.Scale, patch.RotationDegrees, DirectionTransitionSpec.Cut);
                if (patch.Z.HasValue) binding.Actor.SetZIndex(patch.Z.Value);
                if (showing)
                {
                    binding.HideGeneration++;
                    binding.Root.Visible = true;
                    var opacity = added.ContainsKey(id) ? 0 : binding.Visibility.Opacity;
                    binding.Visibility.SetVisible(true);
                    slots.Add(backend.Animate(binding.Presence, patch.Transition,
                        p => binding.Visibility.SetOpacity(Mathf.Lerp(opacity, 1, p))));
                }
            }
            else if (intent is ActorMovePatch move)
                ApplySpace(binding, state, move.Position, move.Scale, move.RotationDegrees, move.Transition);
        }

        private void ApplySpace(Binding binding, ActorState state, DirectionValue<DirectionPoint> position,
            DirectionValue<float> scale, DirectionValue<float> rotation, DirectionTransitionSpec transition)
        {
            var source = binding.CurrentPose(state);
            var destination = source with
            {
                Position = position.HasValue ? position.Value : source.Position,
                Scale = scale.HasValue ? scale.Value : source.Scale,
                RotationDegrees = rotation.HasValue ? rotation.Value : source.RotationDegrees,
            };
            plan.CommitActors[(target!.Id, state.Id)] = destination;
            slots.Add(backend.Animate(binding.Space, transition, p => binding.ApplySpace(destination with
            {
                Position = new DirectionPoint(Mathf.Lerp(source.Position.X, destination.Position.X, p),
                    Mathf.Lerp(source.Position.Y, destination.Position.Y, p)),
                Scale = Mathf.Lerp(source.Scale, destination.Scale, p),
                RotationDegrees = Mathf.Lerp(source.RotationDegrees, destination.RotationDegrees, p),
            })));
        }

        private void ApplyHide(ActorHidePatch patch)
        {
            if (!backend.active.TryGetValue(patch.ActorId, out var binding)) return;
            if (before?.Actors[patch.ActorId].Visible == false)
            {
                waits.Add(WaitForHideReleaseAsync(binding));
                return;
            }
            var hideGeneration = ++binding.HideGeneration;
            var opacity = binding.Visibility.Opacity;
            slots.Add(backend.Animate(binding.Presence, patch.Transition,
                p => binding.Visibility.SetOpacity(Mathf.Lerp(opacity, 0, p)), () =>
                {
                    binding.Space.Finish();
                    binding.AppearanceSlot.Finish();
                    binding.Root.Visible = false;
                    binding.ReleaseTask = ReleaseHiddenAsync(patch.ActorId, binding, hideGeneration);
                }));
            waits.Add(WaitForHideReleaseAsync(binding));
        }

        public void ConfirmCommit() => confirmation.TrySetResult(true);

        private async Task ReleaseHiddenAsync(ActorId id, Binding binding, long generation)
        {
            if (!await confirmation.Task || rolledBack || binding.HideGeneration != generation) return;
            if (backend.active.GetValueOrDefault(id) == binding)
                backend.active.Remove(id);
            await binding.DisposeAsync();
        }

        private static async Task WaitForHideReleaseAsync(Binding binding)
        {
            if (binding.Presence.Current is { } action) await action.Completion;
            await binding.ReleaseTask;
        }

        private async Task ObserveAsync()
        {
            try
            {
                await Task.WhenAll(slots.Select(action => action.Completion).Concat(waits));
                settlement.TrySetResult();
            }
            catch (Exception error) { settlement.TrySetException(error); }
        }

        public async ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            foreach (var slot in slots) slot.Finish();
            await Settlement.WaitAsync(cancellationToken);
        }

        public void Rollback(DirectionCommitToken token)
        {
            rolledBack = true;
            confirmation.TrySetResult(false);
            foreach (var slot in slots) slot.End(DirectionActionEnd.Canceled);
            foreach (var (id, binding) in added)
            { backend.active.Remove(id); binding.Root.Visible = false; }
            foreach (var (id, binding) in removed)
            { backend.active[id] = binding; binding.Root.Visible = true; }
            foreach (var (id, state) in previous)
                if (backend.active.TryGetValue(id, out var binding)) binding.Apply(state);
            settlement.TrySetCanceled();
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            if (!committed || rolledBack) confirmation.TrySetResult(false);
            List<Exception>? failures = null;
            foreach (var binding in (!committed || rolledBack ? added : removed).Values)
                try { await binding.DisposeAsync(); }
                catch (Exception error) { (failures ??= new()).Add(error); }
            foreach (var visual in visuals.Values)
                try { visual.Dispose(); }
                catch (Exception error) { (failures ??= new()).Add(error); }
            visuals.Clear();
            foreach (var retention in retentions)
                try { retention.Dispose(); }
                catch (Exception error) { (failures ??= new()).Add(error); }
            retentions.Clear();
            added.Clear(); removed.Clear(); previous.Clear(); slots.Clear(); waits.Clear();
            if (failures is not null) throw new AggregateException("Actor presentation cleanup failed.", failures);
        }
    }
}
