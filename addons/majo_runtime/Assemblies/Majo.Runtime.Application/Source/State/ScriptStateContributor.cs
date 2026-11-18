#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Script;

namespace Majo.Runtime.State;

public sealed class ScriptStateContributor : IPreparedStateContributor, ILineBoundaryOwnedStateContributor, IScriptBoundaryStateContributor
{
    private static readonly string[] RequiredContributors =
    [
        BuiltInStateKeys.DialogueVariables,
        BuiltInStateKeys.Actors,
        BuiltInStateKeys.AudioBgm,
    ];

    private readonly IScriptPlayback playback;

    public ScriptStateContributor(IScriptPlayback playback)
    {
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public string Key => BuiltInStateKeys.ScriptPlayback;

    public StateScope Scope => StateScope.Game;

    public int SchemaVersion => 2;

    public StateContributorCapability Capability =>
        StateContributorCapability.DurableRollback;

    public IReadOnlyCollection<string> Dependencies => RequiredContributors;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CaptureBoundary(playback.CaptureCheckpoint()));
    }

    public StateRecord CaptureBoundary(ScriptCheckpoint checkpoint)
    {
        var payload = new ScriptPayload(
            checkpoint.FormatVersion,
            checkpoint.ProgramFingerprint,
            checkpoint.Spot.Script.ProjectId,
            checkpoint.Spot.Script.NodeName,
            checkpoint.Spot.LineId,
            checkpoint.Spot.Boundary,
            checkpoint.ResolvedSubstitutions.ToArray(),
            checkpoint.CallStack.ToArray(),
            checkpoint.OperandStack.ToArray());
        return new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StatePayloadJson.Serialize(payload));
    }

    public async ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken) =>
        await playback.ValidateCheckpointAsync(Decode(record), cancellationToken);

    public async ValueTask<IPreparedStateRestore> PrepareRestoreAsync(
        StateRecord? record, RestoreContext context, CancellationToken cancellationToken)
    {
        var checkpoint = Decode(record ?? context.Require(Key));
        if (playback is not IScriptCheckpointRestorePreparation preparer)
            throw new StateCompatibilityException("Script backend does not support prepared checkpoint activation.");
        var origin = context.Operation == StateOperationKind.HotReload
            ? DialoguePresentationOrigin.HotReloadReplay
            : DialoguePresentationOrigin.CheckpointRestore;
        return new PreparedScriptState(await preparer.PrepareCheckpointRestoreAsync(
            checkpoint, cancellationToken, origin));
    }

    private sealed class PreparedScriptState(IPreparedScriptCheckpoint checkpoint)
        : IPreparedStateRestore, IStateRestoreActivation
    {
        public void Commit() => checkpoint.Commit();
        public Task ActivateAsync(CancellationToken cancellationToken) => checkpoint.ActivateAsync(cancellationToken);
        public ValueTask DisposeAsync() => checkpoint.DisposeAsync();
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static ScriptCheckpoint Decode(StateRecord record)
    {
        var payload = StatePayloadJson.Deserialize<ScriptPayload>(record);
        return new ScriptCheckpoint(
            payload.FormatVersion,
            payload.ProgramFingerprint,
            new ScriptPlaybackSpot(
                new ScriptId(payload.ProjectId, payload.NodeName),
                payload.LineId,
                payload.Boundary),
            payload.ResolvedSubstitutions ?? Array.Empty<string>(),
            payload.CallStack,
            payload.OperandStack);
    }

    private sealed record ScriptPayload(
        int FormatVersion,
        string ProgramFingerprint,
        string ProjectId,
        string NodeName,
        string LineId,
        ScriptCheckpointBoundary Boundary,
        string[] ResolvedSubstitutions,
        ScriptCallFrame[]? CallStack = null,
        ScriptStackValue[]? OperandStack = null);
}
