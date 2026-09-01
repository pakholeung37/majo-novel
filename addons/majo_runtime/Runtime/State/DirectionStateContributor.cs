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
        return ValueTask.FromResult(new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StateJson.Serialize(ToPayload(direction.State))));
    }

    public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = FromPayload(StateJson.Deserialize<Payload>(record));
        return ValueTask.CompletedTask;
    }

    public async ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await direction.RestoreAsync(
            FromPayload(StateJson.Deserialize<Payload>(record)),
            cancellationToken);
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
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
        state.Revision);

    private static DirectionState FromPayload(Payload payload)
    {
        var stages = new Dictionary<StageId, StageState>();
        foreach (var stage in payload.Stages)
        {
            var id = new StageId(stage.Id);
            var actors = stage.Actors.ToDictionary(
                actor => new ActorId(actor.Id),
                actor => new ActorState(
                    new ActorId(actor.Id),
                    new DirectionPoint(actor.X, actor.Y),
                    actor.Appearance,
                    actor.Scale,
                    actor.Z));
            stages[id] = new StageState(
                id,
                stage.Backdrop is null
                    ? null
                    : new BackdropState(
                        new ResourceKey(stage.Backdrop.Resource),
                        stage.Backdrop.Fit,
                        stage.Backdrop.Tint),
                actors,
                StageEffectState.Default);
        }
        if (!stages.ContainsKey(StageId.Main)) stages[StageId.Main] = StageState.Create(StageId.Main);
        var views = new Dictionary<ViewId, ViewState>();
        foreach (var view in payload.Views)
        {
            var id = new ViewId(view.Id);
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
            views[id] = new ViewState(
                id,
                source,
                new ViewPose(new DirectionPoint(view.CenterX, view.CenterY), view.Zoom, view.Rotation));
        }
        if (!views.ContainsKey(ViewId.Main))
            views[ViewId.Main] = new ViewState(ViewId.Main, new StageViewSource(StageId.Main), ViewPose.Default);
        return new DirectionState(
            stages,
            views,
            new ScreenState(
                new LayoutId(payload.Layout),
                payload.Bindings.ToDictionary(pair => new LayoutSlotId(pair.Key), pair => new ViewId(pair.Value))),
            StableEffectState.Default,
            payload.Revision);
    }

    private sealed record Payload(
        StagePayload[] Stages,
        ViewPayload[] Views,
        string Layout,
        Dictionary<string, string> Bindings,
        long Revision);

    private sealed record StagePayload(string Id, BackdropPayload? Backdrop, ActorPayload[] Actors);
    private sealed record BackdropPayload(string Resource, BackdropFit Fit, DirectionColor Tint);
    private sealed record ActorPayload(string Id, float X, float Y, string? Appearance, float Scale, int Z);
    private sealed record ViewPayload(string Id, string Source, float CenterX, float CenterY, float Zoom, float Rotation);
}
