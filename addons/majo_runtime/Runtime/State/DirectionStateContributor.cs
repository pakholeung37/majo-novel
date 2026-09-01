#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Resources;

namespace Majo.Runtime.State;

public sealed class DirectionStateContributor : IStateContributor
{
    private readonly IDirectionTransactionCoordinator direction;

    public DirectionStateContributor(IDirectionTransactionCoordinator direction)
    {
        this.direction = direction ?? throw new ArgumentNullException(nameof(direction));
    }

    public string Key => BuiltInStateKeys.Direction;
    public StateScope Scope => StateScope.Game;
    public int SchemaVersion => 1;
    public StateContributorCapability Capability => StateContributorCapability.DurableRollback;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        direction.State.Validate();
        return ValueTask.FromResult(new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StateJson.Serialize(ToPayload(direction.State))));
    }

    public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Decode(record);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await direction.RestoreAsync(
            Decode(record),
            cancellationToken);
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
                    stage.Backdrop.Tint),
            new EffectPayload(stage.Effects.Tint),
            stage.Actors.Values.Select(actor => new ActorPayload(
                actor.Id.Value,
                actor.Position.X,
                actor.Position.Y,
                actor.Appearance,
                actor.Scale,
                actor.Z)).ToArray())).ToArray(),
        state.Views.Values.Select(view => new ViewPayload(
            view.Id.Value,
            view.Source switch
            {
                StageViewSource source => "stage:" + source.StageId.Value,
                CgViewSource source => "cg:" + source.Resource,
                _ => throw new StateSnapshotException("Unsupported Direction View source."),
            },
            view.Pose.Center.X,
            view.Pose.Center.Y,
            view.Pose.Zoom,
            view.Pose.RotationDegrees)).ToArray(),
        state.Screen.Layout.Value,
        state.Screen.Bindings.ToDictionary(pair => pair.Key.Value, pair => pair.Value.Value),
        state.Revision,
        new StableEffectsPayload(new CurtainPayload(
            state.Effects.Curtain.Color,
            state.Effects.Curtain.Opacity)));

    private static DirectionState Decode(StateRecord record)
    {
        try
        {
            if (record.Key != BuiltInStateKeys.Direction ||
                record.Scope != StateScope.Game ||
                record.SchemaVersion != 1)
            {
                throw new StateSnapshotException(
                    $"State record '{BuiltInStateKeys.Direction}' has incompatible metadata.");
            }
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
                    actor.Z));
            foreach (var actor in actors.Values) actor.Validate();
            if (stage.Backdrop is not null)
            {
                new BackdropState(
                    new ResourceKey(stage.Backdrop.Resource),
                    stage.Backdrop.Fit,
                    stage.Backdrop.Tint).Validate();
            }
            (stage.Effects?.Tint ?? DirectionColor.White).Validate();
            stages[id] = new StageState(
                id,
                stage.Backdrop is null
                    ? null
                    : new BackdropState(
                        new ResourceKey(stage.Backdrop.Resource),
                        stage.Backdrop.Fit,
                        stage.Backdrop.Tint),
                actors,
                new StageEffectState(stage.Effects?.Tint ?? DirectionColor.White));
        }
        if (!stages.ContainsKey(StageId.Main))
            throw new StateSnapshotException("Direction state is missing the main Stage.");
        var views = new Dictionary<ViewId, ViewState>();
        foreach (var view in payload.Views)
        {
            ArgumentNullException.ThrowIfNull(view);
            if (string.IsNullOrWhiteSpace(view.Id) || string.IsNullOrWhiteSpace(view.Source))
                throw new StateSnapshotException("Direction view payload is incomplete.");
            var id = new ViewId(view.Id);
            if (views.ContainsKey(id))
                throw new StateSnapshotException($"Direction state contains duplicate view '{view.Id}'.");
            ViewSource source;
            if (view.Source.StartsWith("stage:", StringComparison.Ordinal))
            {
                var stageId = view.Source[6..];
                if (string.IsNullOrWhiteSpace(stageId))
                    throw new StateSnapshotException($"View '{view.Id}' has an empty Stage source.");
                source = new StageViewSource(new StageId(stageId));
            }
            else if (view.Source.StartsWith("cg:", StringComparison.Ordinal))
            {
                var resource = view.Source[3..];
                if (string.IsNullOrWhiteSpace(resource))
                    throw new StateSnapshotException($"View '{view.Id}' has an empty CG source.");
                source = new CgViewSource(new ResourceKey(resource));
            }
            else
            {
                throw new StateSnapshotException(
                    $"View '{view.Id}' has an unsupported source '{view.Source}'.");
            }
            var pose = new ViewPose(
                new DirectionPoint(view.CenterX, view.CenterY), view.Zoom, view.Rotation);
            pose.Validate();
            if (source is StageViewSource stageSource && !stages.ContainsKey(stageSource.StageId))
                throw new StateSnapshotException(
                    $"View '{view.Id}' references missing Stage '{stageSource.StageId}'.");
            views[id] = new ViewState(
                id,
                source,
                pose);
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
                payload.Bindings.ToDictionary(pair => new LayoutSlotId(pair.Key), pair => new ViewId(pair.Value))),
            new StableEffectState(new CurtainState(
                curtain.Color,
                curtain.Opacity)),
            payload.Revision).Validate();
    }

    private sealed record Payload(
        StagePayload[] Stages,
        ViewPayload[] Views,
        string Layout,
        Dictionary<string, string> Bindings,
        long Revision,
        StableEffectsPayload? Effects = null);

    private sealed record StagePayload(
        string Id,
        BackdropPayload? Backdrop,
        EffectPayload? Effects,
        ActorPayload[] Actors);
    private sealed record BackdropPayload(string Resource, BackdropFit Fit, DirectionColor Tint);
    private sealed record EffectPayload(DirectionColor Tint);
    private sealed record StableEffectsPayload(CurtainPayload Curtain);
    private sealed record CurtainPayload(DirectionColor Color, float Opacity);
    private sealed record ActorPayload(string Id, float X, float Y, string? Appearance, float Scale, int Z);
    private sealed record ViewPayload(string Id, string Source, float CenterX, float CenterY, float Zoom, float Rotation);
}
