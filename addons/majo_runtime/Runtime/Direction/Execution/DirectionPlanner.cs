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

        foreach (var directive in directiveList)
        {
            switch (directive)
            {
                case BackdropPatch patch:
                    ApplyBackdrop(patch, stages, writes, transitions, claims);
                    break;
                case ActorPatch patch:
                    ApplyActor(patch, stages, writes, leavePaths, transitions, claims);
                    break;
                case ActorLeavePatch patch:
                    ApplyLeave(patch, stages, writes, leavePaths, transitions);
                    break;
                case ViewPatch patch:
                    ApplyView(patch, views, writes, transitions, claims);
                    break;
                case ScreenPatch patch:
                    screen = ApplyScreen(patch, writes, transitions, claims);
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

        var targetState = new DirectionState(
            DirectionCollections.Freeze(stages),
            DirectionCollections.Freeze(views),
            screen,
            effects,
            checked(beforeState.Revision + 1));

        return new DirectionPlan(
            DirectionTransactionId.New(),
            beforeState,
            targetState,
            transitions.AsReadOnly(),
            cues.AsReadOnly(),
            claims.OrderBy(claim => claim.Kind).ThenBy(claim => claim.SemanticId).ToArray(),
            completion);
    }

    private static void ApplyBackdrop(
        BackdropPatch patch,
        IDictionary<StageId, StageState> stages,
        IDictionary<string, DirectionSourceLocation?> writes,
        ICollection<DirectionTransition> transitions,
        ISet<DirectionResourceClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(patch.Backdrop);
        patch.Backdrop.Validate();
        var paths = new List<string>();
        RegisterWrite(StagePath(patch.StageId, "backdrop"), patch.SourceLocation, writes, paths);
        var stage = GetOrCreateStage(stages, patch.StageId);
        stages[patch.StageId] = stage with { Backdrop = patch.Backdrop };
        claims.Add(new DirectionResourceClaim(
            DirectionResourceKind.Backdrop,
            patch.Backdrop.Resource.ToString()));
        AddTransition(patch.Transition, paths, transitions);
    }

    private static void ApplyActor(
        ActorPatch patch,
        IDictionary<StageId, StageState> stages,
        IDictionary<string, DirectionSourceLocation?> writes,
        ISet<string> leavePaths,
        ICollection<DirectionTransition> transitions,
        ISet<DirectionResourceClaim> claims)
    {
        var stage = GetOrCreateStage(stages, patch.StageId);
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
        AddTransition(patch.Transition, paths, transitions);
    }

    private static void ApplyLeave(
        ActorLeavePatch patch,
        IDictionary<StageId, StageState> stages,
        IDictionary<string, DirectionSourceLocation?> writes,
        ISet<string> leavePaths,
        ICollection<DirectionTransition> transitions)
    {
        if (!stages.TryGetValue(patch.StageId, out var stage))
            throw new DirectionReferenceException($"Stage '{patch.StageId}' does not exist.");
        if (!stage.Actors.ContainsKey(patch.ActorId))
        {
            throw new DirectionReferenceException(
                $"Actor '{patch.ActorId}' does not exist on Stage '{patch.StageId}'.");
        }

        var actorPrefix = ActorPath(patch.StageId, patch.ActorId, string.Empty);
        var existingWrite = writes.FirstOrDefault(pair =>
            pair.Key.StartsWith(actorPrefix, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(existingWrite.Key))
        {
            throw new DirectionConflictException(
                existingWrite.Key,
                existingWrite.Value,
                patch.SourceLocation);
        }

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
        AddTransition(patch.Transition, paths, transitions);
    }

    private static void ApplyView(
        ViewPatch patch,
        IDictionary<ViewId, ViewState> views,
        IDictionary<string, DirectionSourceLocation?> writes,
        ICollection<DirectionTransition> transitions,
        ISet<DirectionResourceClaim> claims)
    {
        var viewExists = views.TryGetValue(patch.ViewId, out var view);
        if (!viewExists && !patch.Source.HasValue)
        {
            throw new DirectionReferenceException(
                $"New View '{patch.ViewId}' requires a Stage or CG source.");
        }

        view ??= new ViewState(
            patch.ViewId,
            patch.Source.Value,
            ViewPose.Default);
        var paths = new List<string>();
        if (!viewExists)
            RegisterWrite(ViewPath(patch.ViewId, "existence"), patch.SourceLocation, writes, paths);

        if (patch.Source.HasValue)
        {
            RegisterWrite(ViewPath(patch.ViewId, "source"), patch.SourceLocation, writes, paths);
            view = view with { Source = patch.Source.Value };
            if (patch.Source.Value is CgViewSource cg)
            {
                claims.Add(new DirectionResourceClaim(
                    DirectionResourceKind.Cg,
                    cg.Resource.ToString()));
            }
        }

        var pose = view.Pose;
        if (patch.Center.HasValue)
        {
            patch.Center.Value.Validate();
            RegisterWrite(ViewPath(patch.ViewId, "center"), patch.SourceLocation, writes, paths);
            pose = pose with { Center = patch.Center.Value };
        }

        if (patch.Zoom.HasValue)
        {
            RegisterWrite(ViewPath(patch.ViewId, "zoom"), patch.SourceLocation, writes, paths);
            pose = pose with { Zoom = patch.Zoom.Value };
        }

        if (patch.RotationDegrees.HasValue)
        {
            RegisterWrite(ViewPath(patch.ViewId, "rotation"), patch.SourceLocation, writes, paths);
            pose = pose with { RotationDegrees = patch.RotationDegrees.Value };
        }

        pose.Validate();
        views[patch.ViewId] = view with { Pose = pose };
        AddTransition(patch.Transition, paths, transitions);
    }

    private static ScreenState ApplyScreen(
        ScreenPatch patch,
        IDictionary<string, DirectionSourceLocation?> writes,
        ICollection<DirectionTransition> transitions,
        ISet<DirectionResourceClaim> claims)
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
            RegisterWrite(
                $"screen/slot/{binding.Key.Value}",
                patch.SourceLocation,
                writes,
                paths);
        }

        claims.Add(new DirectionResourceClaim(DirectionResourceKind.Layout, patch.Layout.Value));
        AddTransition(patch.Transition, paths, transitions);
        return new ScreenState(patch.Layout, DirectionCollections.Freeze(
            patch.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value)));
    }

    private static void ValidateReferences(
        IReadOnlyDictionary<StageId, StageState> stages,
        IReadOnlyDictionary<ViewId, ViewState> views,
        ScreenState screen)
    {
        foreach (var view in views.Values)
        {
            if (view.Source is StageViewSource stageSource &&
                !stages.ContainsKey(stageSource.StageId))
            {
                throw new DirectionReferenceException(
                    $"View '{view.Id}' references unknown Stage '{stageSource.StageId}'.");
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

    private static StageState GetOrCreateStage(
        IDictionary<StageId, StageState> stages,
        StageId stageId)
    {
        if (stages.TryGetValue(stageId, out var stage)) return stage;
        stage = StageState.Create(stageId);
        stages.Add(stageId, stage);
        return stage;
    }

    private static void AddTransition(
        DirectionTransitionSpec transition,
        IReadOnlyList<string> paths,
        ICollection<DirectionTransition> transitions)
    {
        ArgumentNullException.ThrowIfNull(transition);
        transition.Validate();
        if (paths.Count > 0)
            transitions.Add(new DirectionTransition(paths, transition));
    }

    private static void RegisterWrite(
        string path,
        DirectionSourceLocation? location,
        IDictionary<string, DirectionSourceLocation?> writes,
        ICollection<string> directivePaths)
    {
        if (writes.TryGetValue(path, out var previous))
            throw new DirectionConflictException(path, previous, location);
        writes.Add(path, location);
        directivePaths.Add(path);
    }

    private static string StagePath(StageId stageId, string property) =>
        $"stage/{stageId.Value}/{property}";

    private static string ActorPath(StageId stageId, ActorId actorId, string property) =>
        $"actor/{stageId.Value}/{actorId.Value}/{property}";

    private static string ViewPath(ViewId viewId, string property) =>
        $"view/{viewId.Value}/{property}";
}
