#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Execution;

public interface IDirectionPlanner
{
    DirectionPlan Plan(
        DirectionState beforeState,
        IEnumerable<IDirectionDirective> directives,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit);
}

public sealed class DirectionPlanner : IDirectionPlanner
{
    public DirectionPlan Plan(
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
        var leavePaths = new HashSet<string>(StringComparer.Ordinal);
        var transitions = new List<DirectionTransition>();
        var cues = new List<ITransientDirectionCue>();
        var claims = new HashSet<DirectionResourceClaim>();
        var viewCutBaselines = new Dictionary<ViewId, ViewState>();
        var viewSequences = new Dictionary<ViewId, DirectionViewTransitionSequence>();
        var sequencedViews = new HashSet<ViewId>();

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
                    ApplyActor(patch, stages, writes, leavePaths, transitions, claims);
                    break;
                case ActorLeavePatch patch:
                    ApplyLeave(patch, stages, writes, leavePaths, transitions);
                    break;
                case ViewCreatePatch patch:
                    ApplyViewCreate(patch, stages, views, writes);
                    break;
                case ViewPosePatch patch:
                    ViewState? cutBaseline = null;
                    var canSequencePose = patch.Transition.Kind == DirectionTransitionKind.Move &&
                        !sequencedViews.Contains(patch.ViewId) &&
                        viewCutBaselines.TryGetValue(patch.ViewId, out cutBaseline);
                    ApplyViewPose(
                        patch,
                        views,
                        writes,
                        transitions,
                        canSequencePose,
                        sequencedViews);
                    if (canSequencePose)
                    {
                        if (!viewSequences.TryAdd(
                                patch.ViewId,
                                new DirectionViewTransitionSequence(
                                    patch.ViewId,
                                    cutBaseline!,
                                    patch.Transition)))
                        {
                            throw new DirectionBeatException(
                                $"View '{patch.ViewId}' contains more than one sequential pose transition in the same beat.");
                        }
                        sequencedViews.Add(patch.ViewId);
                    }
                    if (patch.Transition.Kind == DirectionTransitionKind.Cut &&
                        views.TryGetValue(patch.ViewId, out var cutState))
                    {
                        viewCutBaselines[patch.ViewId] = cutState;
                    }
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

        ValidateReferences(stages, views, screen);
        ValidateTransitionConflicts(transitions, viewSequences);

        var targetState = new DirectionState(
            DirectionCollections.Freeze(stages),
            DirectionCollections.Freeze(views),
            screen,
            effects,
            checked(beforeState.Revision + 1));
        targetState.Validate();

        return new DirectionPlan(
            DirectionTransactionId.New(),
            beforeState,
            targetState,
            transitions.AsReadOnly(),
            cues.AsReadOnly(),
            claims.OrderBy(claim => claim.Kind).ThenBy(claim => claim.SemanticId).ToArray(),
            completion,
            viewSequences.Values.ToArray());
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
        Dictionary<StageId, StageState> stages,
        Dictionary<string, DirectionSourceLocation?> writes,
        HashSet<string> leavePaths,
        List<DirectionTransition> transitions,
        HashSet<DirectionResourceClaim> claims)
    {
        if (!stages.TryGetValue(patch.StageId, out var stage))
            throw new DirectionReferenceException($"Stage '{patch.StageId}' is not defined.");
        var actors = stage.Actors.ToDictionary(pair => pair.Key, pair => pair.Value);
        var actorExists = actors.TryGetValue(patch.ActorId, out var actor);
        actor ??= ActorState.CreateDefault(patch.ActorId);
        var prefix = ActorPath(patch.StageId, patch.ActorId, string.Empty);
        var presencePath = ActorPath(patch.StageId, patch.ActorId, "presence");
        if (leavePaths.Contains(presencePath))
        {
            throw new DirectionConflictException(
                presencePath,
                writes[presencePath],
                patch.SourceLocation);
        }

        var paths = new List<string>();
        if (!actorExists)
            RegisterWrite(presencePath, patch.SourceLocation, writes, paths);

        if (patch.Position.HasValue)
        {
            patch.Position.Value.Validate();
            RegisterWrite(prefix + "position", patch.SourceLocation, writes, paths);
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
            RegisterWrite(prefix + "scale", patch.SourceLocation, writes, paths);
            actor = actor with { Scale = patch.Scale.Value };
        }
        if (patch.Z.HasValue)
        {
            RegisterWrite(prefix + "z", patch.SourceLocation, writes, paths);
            actor = actor with { Z = patch.Z.Value };
        }

        actor.Validate();
        actors[patch.ActorId] = actor;
        stages[patch.StageId] = stage with { Actors = DirectionCollections.Freeze(actors) };
        AddTransition(patch.Transition, paths, transitions, patch.SourceLocation);
    }

    private static void ApplyLeave(
        ActorLeavePatch patch,
        Dictionary<StageId, StageState> stages,
        Dictionary<string, DirectionSourceLocation?> writes,
        HashSet<string> leavePaths,
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
        leavePaths.Add(ActorPath(patch.StageId, patch.ActorId, "presence"));
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
        List<DirectionTransition> transitions,
        bool allowSequencedPose,
        HashSet<ViewId> sequencedViews)
    {
        if (!views.TryGetValue(patch.ViewId, out var view))
            throw new DirectionReferenceException($"View '{patch.ViewId}' is not defined.");

        var paths = new List<string>();
        var pose = view.Pose;
        if (patch.Center.HasValue)
        {
            patch.Center.Value.Validate();
            RegisterViewPoseWrite(
                ViewPath(patch.ViewId, "center"),
                patch.SourceLocation,
                writes,
                paths,
                allowSequencedPose,
                sequencedViews,
                patch.ViewId);
            pose = pose with { Center = patch.Center.Value };
        }
        if (patch.Zoom.HasValue)
        {
            RegisterViewPoseWrite(
                ViewPath(patch.ViewId, "zoom"),
                patch.SourceLocation,
                writes,
                paths,
                allowSequencedPose,
                sequencedViews,
                patch.ViewId);
            pose = pose with { Zoom = patch.Zoom.Value };
        }
        if (patch.RotationDegrees.HasValue)
        {
            RegisterViewPoseWrite(
                ViewPath(patch.ViewId, "rotation"),
                patch.SourceLocation,
                writes,
                paths,
                allowSequencedPose,
                sequencedViews,
                patch.ViewId);
            pose = pose with { RotationDegrees = patch.RotationDegrees.Value };
        }

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
            patch.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value)));
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
        IReadOnlyList<DirectionTransition> transitions,
        IReadOnlyDictionary<ViewId, DirectionViewTransitionSequence> viewSequences)
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
                if (previous.Component is not null && previous.Spec != transition.Spec &&
                    !IsAllowedViewSequence(component, previous.Spec, transition.Spec, viewSequences))
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
            "stage" or "view" or "actor" => path[..separator],
            "screen" => "screen",
            "effect" => "effect",
            _ => path,
        };
    }

    private static bool IsAllowedViewSequence(
        string component,
        DirectionTransitionSpec previous,
        DirectionTransitionSpec current,
        IReadOnlyDictionary<ViewId, DirectionViewTransitionSequence> viewSequences)
    {
        if (!component.StartsWith("view/", StringComparison.Ordinal) ||
            previous.Kind != DirectionTransitionKind.Cut ||
            current.Kind != DirectionTransitionKind.Move)
            return false;
        var viewId = component["view/".Length..];
        return !string.IsNullOrWhiteSpace(viewId) &&
            viewSequences.ContainsKey(new ViewId(viewId));
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

    private static void RegisterViewPoseWrite(
        string path,
        DirectionSourceLocation? location,
        Dictionary<string, DirectionSourceLocation?> writes,
        List<string> directivePaths,
        bool allowSequencedPose,
        HashSet<ViewId> sequencedViews,
        ViewId viewId)
    {
        if (writes.ContainsKey(path))
        {
            if (!allowSequencedPose)
                throw new DirectionConflictException(path, writes[path], location);
            directivePaths.Add(path);
            return;
        }
        RegisterWrite(path, location, writes, directivePaths);
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
