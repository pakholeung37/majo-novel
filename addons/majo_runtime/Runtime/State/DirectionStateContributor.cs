#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Resources;

namespace Majo.Runtime.State;

public sealed class DirectionStateContributor : IPreparedStateContributor, IStateMutationParticipant
{
    private readonly IDirectionTransactionCoordinator direction;
    private readonly ScenePreparationRegistry? preparation;
    public DirectionStateContributor(IDirectionTransactionCoordinator direction, ScenePreparationRegistry? preparation = null)
    {
        this.preparation = preparation;
        this.direction = direction ?? throw new ArgumentNullException(nameof(direction));
    }

    public void BindStateMutationGate(StateMutationGate gate)
    {
        if (direction is IStateMutationParticipant participant) participant.BindStateMutationGate(gate);
        else throw new StateCompatibilityException("Direction coordinator does not implement the persistent write gate contract.");
    }

    public string Key => BuiltInStateKeys.Direction;
    public StateScope Scope => StateScope.Game;
    public int SchemaVersion => 5;
    public StateContributorCapability Capability => StateContributorCapability.DurableRollback;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = direction.State;
        state.Validate();
        return ValueTask.FromResult(new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StateJson.Serialize(ToPayload(state))));
    }

    public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Decode(record);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IPreparedStateRestore> PrepareRestoreAsync(
        StateRecord? record, RestoreContext context, CancellationToken cancellationToken)
    {
        var target = Decode(record ?? context.Require(Key));
        if (direction is not IDirectionStateReplacementSource replacement)
            throw new StateCompatibilityException("Direction backend does not support prepared replacement.");
        if (preparation is not null)
            await preparation.PrepareAsync(target.Scene.ProjectId, target.Scene.SceneId,
                ScenePreparationReason.Restore, cancellationToken);
        return new PreparedDirectionState(await replacement.PrepareReplacementAsync(target, cancellationToken));
    }

    private sealed class PreparedDirectionState(IPreparedDirectionReplacement prepared)
        : IPreparedStateRestore, IStateRestorePresentation
    {
        public void Commit() => prepared.Commit();
        public Task WaitForPresentationAsync(CancellationToken cancellationToken) =>
            prepared.Settlement.WaitAsync(cancellationToken);
        public ValueTask DisposeAsync() => prepared.DisposeAsync();
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask(direction.ResetAsync(cancellationToken));
    }

    private static Payload ToPayload(DirectionState state) => new(
        state.Stages.Values.Select(stage => new StagePayload(
            stage.Id.Value,
            stage.Backdrop is null
                ? null
                : new BackdropPayload(
                    stage.Backdrop.Resource.ToString(),
                    stage.Backdrop.Fit,
                    stage.Backdrop.Tint, stage.Backdrop.Placement, stage.Backdrop.Distance),
            new EffectPayload(stage.Effects.Tint),
            stage.Actors.Values.Select(actor => new ActorPayload(
                actor.Id.Value,
                actor.Position.X,
                actor.Position.Y,
                actor.Appearance,
                actor.Scale,
                actor.Z, actor.Kind, actor.DefinitionId, actor.Visible, actor.RotationDegrees)).ToArray())).ToArray(),
        state.Views.Values.Select(view => new ViewPayload(
            view.Id.Value,
            view.StageId.Value,
            view.Pose.Center.X,
            view.Pose.Center.Y,
            view.Pose.Zoom,
            view.Pose.RotationDegrees,
            view.Pose.Dolly,
            view.Parallax,
            view.Idle, view.Pose.BackgroundOffset, view.Pose.BackgroundScale)).ToArray(),
        state.Screen.Layout.Value,
        state.Screen.Bindings.ToDictionary(pair => pair.Key.Value, pair => pair.Value.Value),
        state.Revision,
        new StableEffectsPayload(new CurtainPayload(
            state.Effects.Curtain.Color,
            state.Effects.Curtain.Opacity)), state.Scene, state.Screen.Split);

    private static DirectionState Decode(StateRecord record)
    {
        try
        {
            if (record.Key != BuiltInStateKeys.Direction ||
                record.Scope != StateScope.Game ||
                record.SchemaVersion is not (3 or 4 or 5))
            {
                throw new StateSnapshotException(
                    $"State record '{BuiltInStateKeys.Direction}' has incompatible metadata.");
            }
            if (record.SchemaVersion >= 4) ValidateV4Shape(record);
            if (record.SchemaVersion == 5)
                foreach (var view in record.Payload.GetProperty("views").EnumerateArray())
                    if (!view.TryGetProperty("backgroundOffset", out _) || !view.TryGetProperty("backgroundScale", out _) ||
                        !view.GetProperty("parallax").TryGetProperty("useDepthProjection", out _))
                        throw new StateSnapshotException("Direction v5 view is missing background projection state.");
            return FromPayload(StateJson.Deserialize<Payload>(record));
        }
        catch (StateSnapshotException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StateSnapshotException(
                $"State record '{BuiltInStateKeys.Direction}' contains invalid Direction state.",
                exception);
        }
    }

    private static void ValidateV4Shape(StateRecord record)
    {
        if (!record.Payload.TryGetProperty("stages", out var stages) || stages.ValueKind != System.Text.Json.JsonValueKind.Array ||
            !record.Payload.TryGetProperty("views", out var views) || views.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new StateSnapshotException("Direction v4 state payload is incomplete.");
        foreach (var stage in stages.EnumerateArray())
            if (stage.TryGetProperty("backdrop", out var backdrop) && backdrop.ValueKind != System.Text.Json.JsonValueKind.Null &&
                !backdrop.TryGetProperty("distance", out _))
                throw new StateSnapshotException("Direction v4 backdrop is missing distance.");
        foreach (var view in views.EnumerateArray())
            if (!view.TryGetProperty("dolly", out _) || !view.TryGetProperty("parallax", out _) || !view.TryGetProperty("idle", out _))
                throw new StateSnapshotException("Direction v4 view is missing camera environment state.");
    }

    private static DirectionState FromPayload(Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Stages is null || payload.Views is null ||
            payload.Bindings is null)
            throw new StateSnapshotException("Direction state payload is incomplete.");
        if (payload.Revision < 0 || string.IsNullOrWhiteSpace(payload.Layout))
            throw new StateSnapshotException("Direction state payload has an invalid revision or layout.");

        var stages = new Dictionary<StageId, StageState>();
        foreach (var stage in payload.Stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            if (string.IsNullOrWhiteSpace(stage.Id) || stage.Actors is null)
                throw new StateSnapshotException("Direction stage payload is incomplete.");
            var id = new StageId(stage.Id);
            if (stages.ContainsKey(id))
                throw new StateSnapshotException($"Direction state contains duplicate stage '{stage.Id}'.");
            var actors = stage.Actors.ToDictionary(
                actor => new ActorId(actor.Id),
                actor => new ActorState(
                    new ActorId(actor.Id),
                    new DirectionPoint(actor.X, actor.Y),
                    actor.Appearance,
                    actor.Scale,
                    actor.Z)
                {
                    Kind = actor.Kind,
                    DefinitionId = actor.DefinitionId,
                    Visible = actor.Visible,
                    RotationDegrees = actor.RotationDegrees,
                });
            foreach (var actor in actors.Values) actor.Validate();
            if (stage.Backdrop is not null)
            {
                new BackdropState(
                    new ResourceKey(stage.Backdrop.Resource),
                    stage.Backdrop.Fit,
                    stage.Backdrop.Tint)
                { Placement = stage.Backdrop.Placement, Distance = stage.Backdrop.Distance }.Validate();
            }
            (stage.Effects?.Tint ?? DirectionColor.White).Validate();
            stages[id] = new StageState(
                id,
                stage.Backdrop is null
                    ? null
                    : new BackdropState(
                        new ResourceKey(stage.Backdrop.Resource),
                        stage.Backdrop.Fit,
                        stage.Backdrop.Tint)
                    { Placement = stage.Backdrop.Placement, Distance = stage.Backdrop.Distance },
                actors,
                new StageEffectState(stage.Effects?.Tint ?? DirectionColor.White));
        }
        if (!stages.ContainsKey(StageId.Main))
            throw new StateSnapshotException("Direction state is missing the main Stage.");
        var views = new Dictionary<ViewId, ViewState>();
        foreach (var view in payload.Views)
        {
            ArgumentNullException.ThrowIfNull(view);
            if (string.IsNullOrWhiteSpace(view.Id) || string.IsNullOrWhiteSpace(view.StageId))
                throw new StateSnapshotException("Direction view payload is incomplete.");
            var id = new ViewId(view.Id);
            if (views.ContainsKey(id))
                throw new StateSnapshotException($"Direction state contains duplicate view '{view.Id}'.");
            var stageId = new StageId(view.StageId);
            var pose = new ViewPose(
                new DirectionPoint(view.CenterX, view.CenterY), view.Zoom, view.Rotation)
            {
                Dolly = view.Dolly,
                BackgroundOffset = view.BackgroundOffset,
                BackgroundScale = view.BackgroundScale,
            };
            pose.Validate();
            if (!stages.ContainsKey(stageId))
                throw new StateSnapshotException(
                    $"View '{view.Id}' references missing Stage '{stageId}'.");
            views[id] = new ViewState(
                id,
                stageId,
                pose)
            {
                Parallax = view.Parallax ?? ViewParallaxState.Off,
                Idle = view.Idle ?? ViewIdleState.Off,
            };
        }
        if (!views.ContainsKey(ViewId.Main))
            throw new StateSnapshotException("Direction state is missing the main View.");
        var curtain = payload.Effects?.Curtain ?? new CurtainPayload(
            DirectionColor.Black,
            0);
        curtain.Color.Validate();
        if (!float.IsFinite(curtain.Opacity) || curtain.Opacity is < 0 or > 1)
            throw new StateSnapshotException("Direction curtain payload has an invalid opacity.");
        var requiredSlots = DirectionLayoutCatalog.RequiredSlots(new LayoutId(payload.Layout));
        foreach (var pair in payload.Bindings)
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) ||
                !views.ContainsKey(new ViewId(pair.Value)))
                throw new StateSnapshotException("Direction screen bindings contain an invalid view.");
        if (!requiredSlots.All(slot => payload.Bindings.ContainsKey(slot.Value)))
            throw new StateSnapshotException("Direction screen bindings are missing a required layout slot.");

        return new DirectionState(
            stages,
            views,
            new ScreenState(
                new LayoutId(payload.Layout),
                payload.Bindings.ToDictionary(pair => new LayoutSlotId(pair.Key), pair => new ViewId(pair.Value)))
            { Split = payload.Split?.Validate() },
            new StableEffectState(new CurtainState(
                curtain.Color,
                curtain.Opacity)),
            payload.Revision)
        { Scene = payload.Scene ?? new DirectionSceneContext(null, null, 0) }.Validate();
    }

    private sealed record Payload(
        StagePayload[] Stages,
        ViewPayload[] Views,
        string Layout,
        Dictionary<string, string> Bindings,
        long Revision,
        StableEffectsPayload? Effects = null,
        DirectionSceneContext? Scene = null,
        SplitLayout? Split = null);

    private sealed record StagePayload(
        string Id,
        BackdropPayload? Backdrop,
        EffectPayload? Effects,
        ActorPayload[] Actors);
    private sealed record BackdropPayload(string Resource, BackdropFit Fit, DirectionColor Tint,
        BackdropPlacement? Placement = null, float Distance = 1);
    private sealed record EffectPayload(DirectionColor Tint);
    private sealed record StableEffectsPayload(CurtainPayload Curtain);
    private sealed record CurtainPayload(DirectionColor Color, float Opacity);
    private sealed record ActorPayload(string Id, float X, float Y, string? Appearance, float Scale, int Z,
        DirectionActorKind Kind, string? DefinitionId, bool Visible, float RotationDegrees);
    private sealed record ViewPayload(
        string Id,
        string StageId,
        float CenterX,
        float CenterY,
        float Zoom,
        float Rotation,
        float Dolly = 0,
        ViewParallaxState? Parallax = null,
        ViewIdleState? Idle = null,
        DirectionPoint BackgroundOffset = default,
        float BackgroundScale = 1);
}
