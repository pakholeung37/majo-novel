#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Actors;

namespace Majo.Runtime.Direction.Execution;

public interface IDirectionPlanner
{
    DirectionPlan PlanDomain(
        DirectionState beforeState,
        IEnumerable<IDirectionDirective> directives);

    DirectionExecutionPlan Plan(
        DirectionState beforeState,
        IEnumerable<IDirectionDirective> directives,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit);
}

public sealed class DirectionPlanner : IDirectionPlanner
{
    public DirectionPlan PlanDomain(
        DirectionState beforeState,
        IEnumerable<IDirectionDirective> directives)
    {
        return Plan(beforeState, directives, DirectionCompletionBarrier.Commit).DomainPlan;
    }

    public DirectionExecutionPlan Plan(
        DirectionState beforeState,
        IEnumerable<IDirectionDirective> directives,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit)
    {
        ArgumentNullException.ThrowIfNull(beforeState);
        ArgumentNullException.ThrowIfNull(directives);
        beforeState.Validate();
        if (!Enum.IsDefined(completion))
            throw new ArgumentOutOfRangeException(nameof(completion));

        var directiveList = directives.ToArray();
        if (directiveList.Length == 0)
            throw new ArgumentException("A direction plan requires at least one directive.", nameof(directives));
        if (directiveList.Any(directive => directive is null))
            throw new ArgumentException("Direction directives cannot contain null.", nameof(directives));

        var stages = beforeState.Stages.ToDictionary(pair => pair.Key, pair => pair.Value);
        var views = beforeState.Views.ToDictionary(pair => pair.Key, pair => pair.Value);
        var screen = beforeState.Screen;
        var effects = beforeState.Effects;
        var writes = new Dictionary<string, DirectionSourceLocation?>(StringComparer.Ordinal);
        var removePaths = new HashSet<string>(StringComparer.Ordinal);
        var transitions = new List<DirectionTransition>();
        var cues = new List<ITransientDirectionCue>();
        var claims = new HashSet<DirectionResourceClaim>();

        foreach (var directive in directiveList)
        {
            switch (directive)
            {
                case StageCreatePatch patch:
                    ApplyStageCreate(patch, stages, views, writes);
                    break;
                case StageClearPatch patch:
                    ApplyStageClear(patch, stages, writes, transitions);
                    break;
                case StageReleasePatch patch:
                    ApplyStageRelease(patch, stages, views, screen, writes);
                    break;
                case BackdropPatch patch:
                    ApplyBackdrop(patch, stages, writes, transitions, claims);
                    break;
                case ActorPatch patch:
                    ApplyActor(patch, beforeState, stages, writes, removePaths, transitions, claims);
                    break;
                case ActorMovePatch patch:
                    ApplyActorMove(patch, beforeState, stages, writes, transitions);
                    break;
                case ActorHidePatch patch:
                    ApplyActorHide(patch, beforeState, stages, writes, transitions);
                    break;
                case ActorRemovePatch patch:
                    ApplyRemove(patch, stages, writes, removePaths, transitions);
                    break;
                case ViewCreatePatch patch:
                    ApplyViewCreate(patch, stages, views, writes);
                    break;
                case ViewPosePatch patch:
                    ApplyViewPose(patch, views, writes, transitions);
                    break;
                case ViewReleasePatch patch:
                    ApplyViewRelease(patch, views, screen, writes);
                    break;
                case ScreenPatch patch:
                    screen = ApplyScreen(patch, views, writes, transitions, claims);
                    break;
                case CurtainPatch patch:
                    effects = ApplyCurtain(patch, effects, writes, transitions);
                    break;
                case ITransientDirectionCue cue:
                    cues.Add(cue);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported direction directive '{directive.GetType().FullName}'.",
                        nameof(directives));
            }
        }

        foreach (var cue in cues.OfType<ActorFxCue>())
        {
            cue.Validate();
            if (!stages.TryGetValue(cue.Target.StageId, out var stage))
                throw new DirectionReferenceException($"FX Stage '{cue.Target.StageId}' does not exist.");
            if (cue.Target.Slot == ActorFxSlot.Backdrop)
            {
                if (stage.Backdrop is null) throw new DirectionReferenceException("Backdrop FX requires an existing backdrop.");
                continue;
            }
            var id = cue.Target.ActorId!.Value;
            if (!stage.Actors.TryGetValue(id, out var actor))
                throw new DirectionReferenceException($"Actor FX target '{id}' does not exist.");
            ValidateActorIdentity(actor, cue.Kind);
            ActorState? original = null;
            beforeState.Stages.GetValueOrDefault(cue.Target.StageId)?.Actors.TryGetValue(id, out original);
            if (!actor.Visible && original?.Visible != true)
                throw new DirectionReferenceException($"Actor FX target '{id}' is hidden.");
        }

        ValidateReferences(stages, views, screen);
        ValidateTransitionConflicts(transitions);

        var targetState = new DirectionState(
            DirectionCollections.Freeze(stages),
            DirectionCollections.Freeze(views),
            screen,
            effects,
            checked(beforeState.Revision + 1))
        { Scene = beforeState.Scene };
        targetState.Validate();

        var domainPlan = new DirectionPlan(
            DirectionTransactionId.New(),
            beforeState,
            targetState,
            transitions.AsReadOnly(),
            cues.AsReadOnly())
        {
            ViewIntents = directiveList.OfType<ViewPosePatch>().ToArray(),
            ActorIntents = directiveList.OfType<IDurableDirectionPatch>()
                .Where(patch => patch is ActorPatch or ActorMovePatch or ActorHidePatch).ToArray(),
        };

        return new DirectionExecutionPlan(
            domainPlan,
            claims.OrderBy(claim => claim.Kind).ThenBy(claim => claim.SemanticId).ToArray(),
            completion);
    }

    private static void ApplyStageCreate(
        StageCreatePatch patch,
        Dictionary<StageId, StageState> stages,
        Dictionary<ViewId, ViewState> views,
        Dictionary<string, DirectionSourceLocation?> writes)
    {
        if (stages.TryGetValue(patch.StageId, out _))
        {
            if (!views.TryGetValue(patch.DefaultViewId, out var existingView) ||
                existingView.StageId != patch.StageId)
            {
                throw new DirectionReferenceException(
                    $"Stage '{patch.StageId}' exists without its default View '{patch.DefaultViewId}'.");
            }
            return;
        }
        if (views.ContainsKey(patch.DefaultViewId))
        {
            throw new DirectionReferenceException(
                $"Default View '{patch.DefaultViewId}' already belongs to another Stage.");
        }

        var paths = new List<string>();
        RegisterWrite(StagePath(patch.StageId, "existence"), patch.SourceLocation, writes, paths);
        RegisterWrite(ViewPath(patch.DefaultViewId, "existence"), patch.SourceLocation, writes, paths);
        stages.Add(patch.StageId, StageState.Create(patch.StageId));
        views.Add(
            patch.DefaultViewId,
            new ViewState(patch.DefaultViewId, patch.StageId, ViewPose.Default));
    }

    private static void ApplyStageClear(
        StageClearPatch patch,
        Dictionary<StageId, StageState> stages,
        Dictionary<string, DirectionSourceLocation?> writes,
        List<DirectionTransition> transitions)
    {
        if (!stages.TryGetValue(patch.StageId, out var stage))
            throw new DirectionReferenceException($"Stage '{patch.StageId}' does not exist.");
        EnsureNoWritesWithPrefix(StagePath(patch.StageId, string.Empty), writes, patch.SourceLocation);
        EnsureNoWritesWithPrefix(ActorStagePath(patch.StageId), writes, patch.SourceLocation);

        var paths = new List<string>();
        RegisterWrite(StagePath(patch.StageId, "backdrop"), patch.SourceLocation, writes, paths);
        foreach (var actorId in stage.Actors.Keys)
        {
            RegisterWrite(
                ActorPath(patch.StageId, actorId, "presence"),
                patch.SourceLocation,
                writes,
                paths);
        }
        stages[patch.StageId] = stage with
        {
            Backdrop = null,
            Actors = DirectionCollections.Empty<ActorId, ActorState>(),
        };
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
    }

    private static void ApplyStageRelease(
        StageReleasePatch patch,
        Dictionary<StageId, StageState> stages,
        Dictionary<ViewId, ViewState> views,
        ScreenState screen,
        Dictionary<string, DirectionSourceLocation?> writes)
    {
        if (patch.StageId == StageId.Main)
            throw new DirectionReferenceException("The main Stage cannot be released.");
        if (!stages.ContainsKey(patch.StageId))
            throw new DirectionReferenceException($"Stage '{patch.StageId}' does not exist.");

        var boundViews = views.Values
            .Where(view => view.StageId == patch.StageId)
            .Select(view => view.Id)
            .ToArray();
        var displayed = screen.Bindings.FirstOrDefault(pair => boundViews.Contains(pair.Value));
        if (!string.IsNullOrWhiteSpace(displayed.Key.Value))
        {
            throw new DirectionReferenceException(
                $"Stage '{patch.StageId}' cannot be released while Screen slot '{displayed.Key}' displays it.");
        }
        EnsureNoWritesWithPrefix(StagePath(patch.StageId, string.Empty), writes, patch.SourceLocation);
        EnsureNoWritesWithPrefix(ActorStagePath(patch.StageId), writes, patch.SourceLocation);
        foreach (var viewId in boundViews)
            EnsureNoWritesWithPrefix(ViewPath(viewId, string.Empty), writes, patch.SourceLocation);

        var paths = new List<string>();
        RegisterWrite(StagePath(patch.StageId, "existence"), patch.SourceLocation, writes, paths);
        foreach (var viewId in boundViews)
        {
            RegisterWrite(ViewPath(viewId, "existence"), patch.SourceLocation, writes, paths);
            views.Remove(viewId);
        }
        stages.Remove(patch.StageId);
    }

    private static void ApplyBackdrop(
        BackdropPatch patch,
        Dictionary<StageId, StageState> stages,
        Dictionary<string, DirectionSourceLocation?> writes,
        List<DirectionTransition> transitions,
        HashSet<DirectionResourceClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(patch.Backdrop);
        patch.Backdrop.Validate();
        if (!stages.TryGetValue(patch.StageId, out var stage))
            throw new DirectionReferenceException($"Stage '{patch.StageId}' is not defined.");
        var paths = new List<string>();
        RegisterWrite(StagePath(patch.StageId, "backdrop"), patch.SourceLocation, writes, paths);
        stages[patch.StageId] = stage with { Backdrop = patch.Backdrop };
        claims.Add(new DirectionResourceClaim(
            DirectionResourceKind.Backdrop,
            patch.Backdrop.Resource.ToString()));
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
    }

    private static void ApplyActor(
        ActorPatch patch,
        DirectionState beforeState,
        Dictionary<StageId, StageState> stages,
        Dictionary<string, DirectionSourceLocation?> writes,
        HashSet<string> removePaths,
        List<DirectionTransition> transitions,
        HashSet<DirectionResourceClaim> claims)
    {
        if (!stages.TryGetValue(patch.StageId, out var stage))
            throw new DirectionReferenceException($"Stage '{patch.StageId}' is not defined.");
        var actors = stage.Actors.ToDictionary(pair => pair.Key, pair => pair.Value);
        var actorExists = actors.TryGetValue(patch.ActorId, out var actor);
        actor ??= ActorState.CreateDefault(patch.ActorId) with { Kind = patch.Kind, DefinitionId = patch.DefinitionId };
        ValidateActorIdentity(actor, patch.Kind, patch.DefinitionId);
        ActorState? original = null;
        beforeState.Stages.GetValueOrDefault(patch.StageId)?.Actors.TryGetValue(patch.ActorId, out original);
        var showing = original is null || !original.Visible;
        var prefix = ActorPath(patch.StageId, patch.ActorId, string.Empty);
        var presencePath = ActorPath(patch.StageId, patch.ActorId, "presence");
        if (removePaths.Contains(presencePath))
        {
            throw new DirectionConflictException(
                presencePath,
                writes[presencePath],
                patch.SourceLocation);
        }

        var paths = new List<string>();
        if (!actorExists)
            RegisterWrite(presencePath, patch.SourceLocation, writes, paths);
        if (showing)
        {
            RegisterWrite(prefix + "visibility", patch.SourceLocation, writes, paths);
            actor = actor with { Visible = true };
        }
        var hasSpace = patch.Position.HasValue || patch.Scale.HasValue || patch.RotationDegrees.HasValue;
        if (hasSpace) RegisterWrite(prefix + "space", patch.SourceLocation, writes, paths);

        if (patch.Position.HasValue)
        {
            patch.Position.Value.Validate();

            actor = actor with { Position = patch.Position.Value };
        }
        if (patch.Appearance.HasValue)
        {
            var appearance = patch.Appearance.Value;
            if (appearance is not null && string.IsNullOrWhiteSpace(appearance))
                throw new ArgumentException("Actor appearance cannot be blank.", nameof(patch));
            RegisterWrite(prefix + "appearance", patch.SourceLocation, writes, paths);
            actor = actor with { Appearance = appearance };
            if (appearance is not null)
            {
                claims.Add(new DirectionResourceClaim(
                    DirectionResourceKind.ActorAppearance,
                    $"{patch.ActorId}/{appearance}"));
            }
        }
        if (patch.Scale.HasValue)
        {

            actor = actor with { Scale = patch.Scale.Value };
        }
        if (patch.RotationDegrees.HasValue)
            actor = actor with { RotationDegrees = patch.RotationDegrees.Value };
        if (patch.Z.HasValue)
        {
            RegisterWrite(prefix + "z", patch.SourceLocation, writes, paths);
            actor = actor with { Z = patch.Z.Value };
        }

        if (patch.Kind == DirectionActorKind.SceneObject)
            claims.Add(new DirectionResourceClaim(DirectionResourceKind.ObjectDefinition, actor.DefinitionId!));
        actor.Validate();
        actors[patch.ActorId] = actor;
        stages[patch.StageId] = stage with { Actors = DirectionCollections.Freeze(actors) };
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
    }

    private static void ValidateActorIdentity(ActorState actor, DirectionActorKind kind, string? definitionId = null)
    {
        if (actor.Kind != kind)
            throw new DirectionReferenceException($"Actor '{actor.Id}' is {actor.Kind}, not {kind}.");
        if (definitionId is not null && actor.DefinitionId != definitionId)
            throw new DirectionReferenceException($"Actor '{actor.Id}' cannot change its definition.");
    }

    private static ActorState RequireExistingActor(DirectionState state, StageId stage, ActorId id,
        DirectionActorKind kind, bool visible)
    {
        if (!state.Stages.TryGetValue(stage, out var target) || !target.Actors.TryGetValue(id, out var actor))
            throw new DirectionReferenceException($"Actor '{id}' does not exist on Stage '{stage}'.");
        ValidateActorIdentity(actor, kind);
        if (visible && !actor.Visible)
            throw new DirectionReferenceException($"Actor '{id}' must be visible before this beat starts.");
        return actor;
    }

    private static void ApplyActorMove(ActorMovePatch patch, DirectionState before,
        Dictionary<StageId, StageState> stages, Dictionary<string, DirectionSourceLocation?> writes,
        List<DirectionTransition> transitions)
    {
        RequireExistingActor(before, patch.StageId, patch.ActorId, patch.Kind, true);
        if (!patch.Position.HasValue && !patch.Scale.HasValue && !patch.RotationDegrees.HasValue)
            throw new ArgumentException("Actor move requires at, scale or rotation.");
        if (patch.Transition.Kind != DirectionTransitionId.Move)
            throw new ArgumentException("Actor move requires a movement transition.");
        var stage = stages[patch.StageId];
        var actor = stage.Actors[patch.ActorId];
        var paths = new List<string>();
        RegisterWrite(ActorPath(patch.StageId, patch.ActorId, "space"), patch.SourceLocation, writes, paths);
        actor = actor with
        {
            Position = patch.Position.HasValue ? patch.Position.Value : actor.Position,
            Scale = patch.Scale.HasValue ? patch.Scale.Value : actor.Scale,
            RotationDegrees = patch.RotationDegrees.HasValue ? patch.RotationDegrees.Value : actor.RotationDegrees,
        };
        actor.Validate();
        var actors = stage.Actors.ToDictionary(pair => pair.Key, pair => pair.Value);
        actors[patch.ActorId] = actor;
        stages[patch.StageId] = stage with { Actors = DirectionCollections.Freeze(actors) };
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
    }

    private static void ApplyActorHide(ActorHidePatch patch, DirectionState before,
        Dictionary<StageId, StageState> stages, Dictionary<string, DirectionSourceLocation?> writes,
        List<DirectionTransition> transitions)
    {
        if (patch.Transition.Kind != DirectionTransitionId.Cut && patch.Transition.Kind != DirectionTransitionId.Fade)
            throw new DirectionException("Actor hide supports only cut or fade.");
        var visibilityPath = ActorPath(patch.StageId, patch.ActorId, "visibility");
        if (writes.TryGetValue(visibilityPath, out var writer))
            throw new DirectionConflictException(visibilityPath, writer, patch.SourceLocation);
        var original = RequireExistingActor(before, patch.StageId, patch.ActorId, patch.Kind, false);
        var stage = stages[patch.StageId];
        var paths = new List<string>();
        RegisterWrite(ActorPath(patch.StageId, patch.ActorId, "visibility"), patch.SourceLocation, writes, paths);
        var actors = stage.Actors.ToDictionary(pair => pair.Key, pair => pair.Value);
        actors[patch.ActorId] = actors[patch.ActorId] with { Visible = false };
        stages[patch.StageId] = stage with { Actors = DirectionCollections.Freeze(actors) };
        if (original.Visible) AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
    }

    private static void ApplyRemove(
        ActorRemovePatch patch,
        Dictionary<StageId, StageState> stages,
        Dictionary<string, DirectionSourceLocation?> writes,
        HashSet<string> removePaths,
        List<DirectionTransition> transitions)
    {
        if (!stages.TryGetValue(patch.StageId, out var stage))
            throw new DirectionReferenceException($"Stage '{patch.StageId}' does not exist.");
        if (!stage.Actors.ContainsKey(patch.ActorId))
        {
            throw new DirectionReferenceException(
                $"Actor '{patch.ActorId}' does not exist on Stage '{patch.StageId}'.");
        }

        var actorPrefix = ActorPath(patch.StageId, patch.ActorId, string.Empty);
        EnsureNoWritesWithPrefix(actorPrefix, writes, patch.SourceLocation);
        var paths = new List<string>();
        RegisterWrite(
            ActorPath(patch.StageId, patch.ActorId, "presence"),
            patch.SourceLocation,
            writes,
            paths);
        removePaths.Add(ActorPath(patch.StageId, patch.ActorId, "presence"));
        var actors = stage.Actors.ToDictionary(pair => pair.Key, pair => pair.Value);
        actors.Remove(patch.ActorId);
        stages[patch.StageId] = stage with { Actors = DirectionCollections.Freeze(actors) };
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
    }

    private static void ApplyViewCreate(
        ViewCreatePatch patch,
        Dictionary<StageId, StageState> stages,
        Dictionary<ViewId, ViewState> views,
        Dictionary<string, DirectionSourceLocation?> writes)
    {
        if (!stages.ContainsKey(patch.StageId))
            throw new DirectionReferenceException($"Stage '{patch.StageId}' is not defined.");
        if (views.TryGetValue(patch.ViewId, out var existing))
        {
            if (existing.StageId != patch.StageId)
            {
                throw new DirectionReferenceException(
                    $"View '{patch.ViewId}' is already bound to Stage '{existing.StageId}'.");
            }
            return;
        }

        var paths = new List<string>();
        RegisterWrite(ViewPath(patch.ViewId, "existence"), patch.SourceLocation, writes, paths);
        views.Add(patch.ViewId, new ViewState(patch.ViewId, patch.StageId, ViewPose.Default));
    }

    private static void ApplyViewPose(
        ViewPosePatch patch,
        Dictionary<ViewId, ViewState> views,
        Dictionary<string, DirectionSourceLocation?> writes,
        List<DirectionTransition> transitions)
    {
        if (!views.TryGetValue(patch.ViewId, out var view))
            throw new DirectionReferenceException($"View '{patch.ViewId}' is not defined.");
        var paths = new List<string>();
        if (!patch.Center.HasValue && !patch.Zoom.HasValue && !patch.RotationDegrees.HasValue) return;
        RegisterWrite(ViewPath(patch.ViewId, "pose"), patch.SourceLocation, writes, paths);
        var pose = view.Pose with
        {
            Center = patch.Center.HasValue ? patch.Center.Value : view.Pose.Center,
            Zoom = patch.Zoom.HasValue ? patch.Zoom.Value : view.Pose.Zoom,
            RotationDegrees = patch.RotationDegrees.HasValue ? patch.RotationDegrees.Value : view.Pose.RotationDegrees,
        };
        pose.Validate();
        views[patch.ViewId] = view with { Pose = pose };
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
    }

    private static void ApplyViewRelease(
        ViewReleasePatch patch,
        Dictionary<ViewId, ViewState> views,
        ScreenState screen,
        Dictionary<string, DirectionSourceLocation?> writes)
    {
        if (!views.TryGetValue(patch.ViewId, out var view))
            throw new DirectionReferenceException($"View '{patch.ViewId}' does not exist.");
        if (patch.ViewId == ViewId.Main || patch.ViewId.Value == view.StageId.Value)
        {
            throw new DirectionReferenceException(
                $"Default View '{patch.ViewId}' must be released with its Stage.");
        }
        var displayed = screen.Bindings.FirstOrDefault(pair => pair.Value == patch.ViewId);
        if (!string.IsNullOrWhiteSpace(displayed.Key.Value))
        {
            throw new DirectionReferenceException(
                $"View '{patch.ViewId}' cannot be released while Screen slot '{displayed.Key}' displays it.");
        }
        EnsureNoWritesWithPrefix(ViewPath(patch.ViewId, string.Empty), writes, patch.SourceLocation);
        var paths = new List<string>();
        RegisterWrite(ViewPath(patch.ViewId, "existence"), patch.SourceLocation, writes, paths);
        views.Remove(patch.ViewId);
    }

    private static ScreenState ApplyScreen(
        ScreenPatch patch,
        Dictionary<ViewId, ViewState> views,
        Dictionary<string, DirectionSourceLocation?> writes,
        List<DirectionTransition> transitions,
        HashSet<DirectionResourceClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(patch.Bindings);
        patch.Split?.Validate();
        if (patch.Split is not null && !patch.Layout.Value.Equals("split", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Split parameters require the split layout.");
        if (patch.Bindings.Count == 0)
            throw new ArgumentException("A Screen patch requires at least one View binding.");

        var paths = new List<string>();
        RegisterWrite("screen/layout", patch.SourceLocation, writes, paths);
        var required = DirectionLayoutCatalog.RequiredSlots(patch.Layout);
        if (patch.Bindings.Count != required.Count ||
            required.Any(slot => !patch.Bindings.ContainsKey(slot)))
        {
            throw new DirectionReferenceException(
                $"Layout '{patch.Layout}' requires exactly these slots: " +
                string.Join(", ", required.Select(slot => slot.Value)));
        }
        if (patch.Bindings.Keys.Any(slot => !required.Contains(slot)))
        {
            throw new DirectionReferenceException(
                $"Layout '{patch.Layout}' contains an unknown slot.");
        }
        foreach (var binding in patch.Bindings)
        {
            if (!views.ContainsKey(binding.Value))
            {
                throw new DirectionReferenceException(
                    $"Screen slot '{binding.Key}' references undefined View '{binding.Value}'.");
            }
            RegisterWrite(
                $"screen/slot/{binding.Key.Value}",
                patch.SourceLocation,
                writes,
                paths);
        }

        claims.Add(new DirectionResourceClaim(DirectionResourceKind.Layout, patch.Layout.Value));
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
        return new ScreenState(patch.Layout, DirectionCollections.Freeze(
            patch.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value))) { Split = patch.Split };
    }

    private static StableEffectState ApplyCurtain(
        CurtainPatch patch,
        StableEffectState effects,
        Dictionary<string, DirectionSourceLocation?> writes,
        List<DirectionTransition> transitions)
    {
        ArgumentNullException.ThrowIfNull(patch.Curtain);
        patch.Curtain.Validate();
        var paths = new List<string>();
        RegisterWrite("effect/curtain", patch.SourceLocation, writes, paths);
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
        return new StableEffectState(patch.Curtain);
    }

    private static void ValidateReferences(
        Dictionary<StageId, StageState> stages,
        Dictionary<ViewId, ViewState> views,
        ScreenState screen)
    {
        foreach (var view in views.Values)
        {
            if (!stages.ContainsKey(view.StageId))
            {
                throw new DirectionReferenceException(
                    $"View '{view.Id}' references unknown Stage '{view.StageId}'.");
            }
        }
        foreach (var binding in screen.Bindings)
        {
            if (!views.ContainsKey(binding.Value))
            {
                throw new DirectionReferenceException(
                    $"Screen slot '{binding.Key}' references unknown View '{binding.Value}'.");
            }
        }
    }

    private static void ValidateTransitionConflicts(
        IReadOnlyList<DirectionTransition> transitions)
    {
        var seen = new List<(string Component, DirectionTransitionSpec Spec, DirectionSourceLocation? Location)>();
        foreach (var transition in transitions)
        {
            transition.Spec.Validate();
            foreach (var path in transition.StablePaths)
            {
                var component = TransitionComponent(path);
                if (component is null) continue;
                var previous = seen.FirstOrDefault(item =>
                    item.Component == "*" || component == "*" ||
                    item.Component == component);
                if (previous.Component is not null && previous.Spec != transition.Spec)
                {
                    throw new DirectionBeatException(
                        $"Direction component '{component}' has conflicting transition specifications " +
                        $"in the same beat ({previous.Location?.ToString() ?? "unknown"}, " +
                        $"{transition.SourceLocation?.ToString() ?? "unknown"}).");
                }
                seen.Add((component, transition.Spec, transition.SourceLocation));
            }
        }
    }

    private static string? TransitionComponent(string path)
    {
        if (path == "*") return "*";
        var separator = path.LastIndexOf('/');
        if (separator <= 0) return path;
        var kindSeparator = path.IndexOf('/');
        var kind = kindSeparator > 0 ? path[..kindSeparator] : path;
        return kind switch
        {
            "stage" or "view" => path[..separator],
            "actor" => path,
            "screen" => "screen",
            "effect" => "effect",
            _ => path,
        };
    }

    private static void EnsureNoWritesWithPrefix(
        string prefix,
        IReadOnlyDictionary<string, DirectionSourceLocation?> writes,
        DirectionSourceLocation? location)
    {
        var existing = writes.FirstOrDefault(pair =>
            pair.Key.StartsWith(prefix, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(existing.Key))
            throw new DirectionConflictException(existing.Key, existing.Value, location);
    }

    private static void AddTransition(
        DirectionTransitionSpec transition,
        List<string> paths,
        List<DirectionTransition> transitions,
        DirectionSourceLocation? sourceLocation)
    {
        ArgumentNullException.ThrowIfNull(transition);
        transition.Validate();
        if (paths.Count > 0)
            transitions.Add(new DirectionTransition(paths, transition, sourceLocation));
    }

    private static void RegisterWrite(
        string path,
        DirectionSourceLocation? location,
        Dictionary<string, DirectionSourceLocation?> writes,
        List<string> directivePaths)
    {
        if (writes.TryGetValue(path, out var previous))
            throw new DirectionConflictException(path, previous, location);
        writes.Add(path, location);
        directivePaths.Add(path);
    }

    private static string StagePath(StageId stageId, string property) =>
        $"stage/{stageId.Value}/{property}";

    private static string ActorStagePath(StageId stageId) =>
        $"actor/{stageId.Value}/";

    private static string ActorPath(StageId stageId, ActorId actorId, string property) =>
        $"actor/{stageId.Value}/{actorId.Value}/{property}";

    private static string ViewPath(ViewId viewId, string property) =>
        $"view/{viewId.Value}/{property}";
}
