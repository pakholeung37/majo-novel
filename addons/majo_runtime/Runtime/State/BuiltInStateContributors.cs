#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Dialogue;
using Majo.Runtime.History;
using Majo.Runtime.Script;

namespace Majo.Runtime.State;

public static class BuiltInStateKeys
{
    public const string DialogueVariables = "majo.dialogue.variables";
    public const string Actors = "majo.actors";
    public const string AudioBgm = "majo.audio.bgm";
    public const string ScriptPlayback = "majo.script.playback";
    public const string SessionHistory = "majo.session.history";
    public const string Direction = "majo.direction";
}

public sealed class SessionHistoryStateContributor : IImmediateStateContributor
{
    private readonly IHistoryManager history;

    public SessionHistoryStateContributor(IHistoryManager history)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public string Key => BuiltInStateKeys.SessionHistory;

    public StateScope Scope => StateScope.Session;

    public int SchemaVersion => 2;

    public StateContributorCapability Capability => StateContributorCapability.Transient;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StateJson.Serialize(Array.Empty<object>())));
    }

    public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        history.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        history.Clear();
        return ValueTask.CompletedTask;
    }
}

public sealed class DialogueVariablesStateContributor : IPreparedStateContributor, IStateMutationParticipant
{
    public void BindStateMutationGate(StateMutationGate gate)
    {
        if (variables is IStateMutationParticipant participant) participant.BindStateMutationGate(gate);
        else throw new StateCompatibilityException("Dialogue variables do not implement the persistent write gate contract.");
    }
    private readonly IDialogueVariables variables;

    public DialogueVariablesStateContributor(IDialogueVariables variables)
    {
        this.variables = variables ?? throw new ArgumentNullException(nameof(variables));
    }

    public string Key => BuiltInStateKeys.DialogueVariables;

    public StateScope Scope => StateScope.Game;

    public int SchemaVersion => 1;

    public StateContributorCapability Capability =>
        StateContributorCapability.DurableRollback;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = variables.Capture()
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => VariableEntry.From(pair.Key, pair.Value))
            .ToArray();
        return ValueTask.FromResult(CreateRecord(new VariablesPayload(entries)));
    }

    public ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Decode(record);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken) =>
        await variables.ResetAsync(cancellationToken);

    public ValueTask<IPreparedStateRestore> PrepareRestoreAsync(
        StateRecord? record, RestoreContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Decode(record ?? context.Require(Key));
        return ValueTask.FromResult<IPreparedStateRestore>(
            new PreparedStateRestore(() => variables.ReplaceAll(target)));
    }

    private StateRecord CreateRecord(VariablesPayload payload) =>
        new(Key, Scope, SchemaVersion, StateJson.Serialize(payload));

    private Dictionary<string, DialogueValue> Decode(StateRecord record)
    {
        var payload = StateJson.Deserialize<VariablesPayload>(record);
        var values = new Dictionary<string, DialogueValue>(StringComparer.Ordinal);
        foreach (var entry in payload.Entries ?? Array.Empty<VariableEntry>())
        {
            var name = DialogueVariables.NormalizeName(entry.Name);
            if (!values.TryAdd(name, entry.ToValue()))
            {
                throw new StateSnapshotException(
                    $"State record '{Key}' contains duplicate variable '{name}'.");
            }
        }

        return values;
    }

    private sealed record VariablesPayload(VariableEntry[] Entries);

    private sealed record VariableEntry(
        string Name,
        DialogueValueKind Kind,
        double NumberValue,
        string? StringValue,
        bool BooleanValue)
    {
        public static VariableEntry From(string name, DialogueValue value) => new(
            name,
            value.Kind,
            value.NumberValue,
            value.StringValue,
            value.BooleanValue);

        public DialogueValue ToValue() => Kind switch
        {
            DialogueValueKind.Number => DialogueValue.FromNumber(NumberValue),
            DialogueValueKind.Text => DialogueValue.FromString(
                StringValue ?? throw new StateSnapshotException(
                    $"String variable '{Name}' has no value.")),
            DialogueValueKind.Boolean => DialogueValue.FromBoolean(BooleanValue),
            _ => throw new StateSnapshotException(
                $"Variable '{Name}' has unsupported kind '{Kind}'."),
        };
    }
}

public sealed class ActorStateContributor : IPreparedStateContributor, IStateMutationParticipant
{
    private readonly IActorManager actors;

    public ActorStateContributor(IActorManager actors)
    {
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
    }

    public void BindStateMutationGate(StateMutationGate gate)
    {
        if (actors is IStateMutationParticipant participant) participant.BindStateMutationGate(gate);
        else throw new StateCompatibilityException("Actor manager does not implement the persistent write gate contract.");
    }

    public string Key => BuiltInStateKeys.Actors;

    public StateScope Scope => StateScope.Game;

    public int SchemaVersion => 2;

    public StateContributorCapability Capability =>
        StateContributorCapability.DurableRollback;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = actors.Actors
            .Select(actor => new ActorEntry(
                actor.Id,
                actor.DefinitionId,
                actor.FactoryId,
                actor is IBackgroundActor background ? actors.GetBackgroundSlot(background) : null,
                actor.Appearance,
                actor.Visible,
                actor.Position.X,
                actor.Position.Y,
                actor is ICharacterActor character ? character.Scale.X : null,
                actor is ICharacterActor scaledCharacter ? scaledCharacter.Scale.Y : null,
                actor.Tint.R,
                actor.Tint.G,
                actor.Tint.B,
                actor.Opacity,
                actor.ZIndex))
            .ToArray();
        return ValueTask.FromResult(new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StateJson.Serialize(new ActorsPayload(entries))));
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
        var entries = Decode(record ?? context.Require(Key));
        if (actors is not IActorReplacementSource replacement)
            throw new StateCompatibilityException("Actor backend does not support prepared replacement.");
        var prepared = await replacement.PrepareReplacementAsync(entries.Select(entry =>
            new ActorSpawnRequest(entry.Id, entry.FactoryId, entry.Appearance,
                requiredFactoryId: entry.FactoryId, creationMode: ActorCreationMode.Dormant)
            { DefinitionId = entry.DefinitionId }).ToArray(), cancellationToken);
        try
        {
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                var actor = prepared.Actors[index];
                if (actor is IBackgroundActor background)
                {
                    await using var appearance = await background.PrepareBackgroundAppearanceAsync(entry.Appearance, cancellationToken);
                    appearance.Commit();
                    appearance.ConfirmCommit();
                    actors.SetBackgroundSlot(background, entry.Slot ?? BackgroundSlotIds.Main);
                }
                else
                {
                    await actor.PrepareAppearanceAsync(entry.Appearance, cancellationToken);
                    actor.SetAppearance(entry.Appearance);
                }
                actor.SetPosition(new Vector2(entry.PositionX, entry.PositionY));
                if (actor is ICharacterActor character && entry.ScaleX is { } x && entry.ScaleY is { } y)
                    character.SetScale(new Vector2(x, y));
                actor.SetTint(new Color(entry.TintR, entry.TintG, entry.TintB));
                actor.SetOpacity(entry.Opacity);
                actor.SetZIndex(entry.ZIndex);
                actor.SetVisible(false);
            }
            return new PreparedStateRestore(() =>
            {
                prepared.Commit();
                for (var index = 0; index < entries.Length; index++)
                    prepared.Actors[index].SetVisible(entries[index].Visible);
            }, prepared.DisposeAsync);
        }
        catch { await prepared.DisposeAsync(); throw; }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken) =>
        await actors.ResetAsync(cancellationToken);

    private ActorEntry[] Decode(StateRecord record)
    {
        var payload = StateJson.Deserialize<ActorsPayload>(record);
        var entries = payload.Entries ?? Array.Empty<ActorEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.DefinitionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.FactoryId);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Appearance);
            if (entry.FactoryId == ActorFactoryIds.Background)
            {
                try
                {
                    _ = BackgroundSlotIds.Normalize(entry.Slot ?? BackgroundSlotIds.Main);
                }
                catch (ActorException exception)
                {
                    throw new StateSnapshotException(
                        $"Background actor '{entry.Id}' contains an invalid slot.",
                        exception);
                }
            }
            if (!ids.Add(entry.Id))
            {
                throw new StateSnapshotException(
                    $"State record '{Key}' contains duplicate actor '{entry.Id}'.");
            }

            if (!actors.CanCreate(new ActorSpawnRequest(
                    entry.Id,
                    entry.FactoryId,
                    entry.Appearance,
                    requiredFactoryId: entry.FactoryId)
            { DefinitionId = entry.DefinitionId }))
            {
                throw new StateSnapshotException(
                    $"Actor '{entry.Id}' cannot be restored by factory '{entry.FactoryId}'.");
            }

            if (!float.IsFinite(entry.PositionX) || !float.IsFinite(entry.PositionY) ||
                entry.ScaleX is { } scaleX && (!float.IsFinite(scaleX) || scaleX <= 0) ||
                entry.ScaleY is { } scaleY && (!float.IsFinite(scaleY) || scaleY <= 0) ||
                !float.IsFinite(entry.TintR) || !float.IsFinite(entry.TintG) ||
                !float.IsFinite(entry.TintB) || !float.IsFinite(entry.Opacity) ||
                entry.Opacity is < 0 or > 1 ||
                entry.ZIndex is < ActorZIndexRange.Minimum or > ActorZIndexRange.Maximum)
            {
                throw new StateSnapshotException(
                    $"Actor '{entry.Id}' contains invalid visual state.");
            }
        }

        return entries;
    }

    private sealed record ActorsPayload(ActorEntry[] Entries);

    private sealed record ActorEntry(
        string Id,
        string DefinitionId,
        string FactoryId,
        string? Slot,
        string Appearance,
        bool Visible,
        float PositionX,
        float PositionY,
        float? ScaleX,
        float? ScaleY,
        float TintR,
        float TintG,
        float TintB,
        float Opacity,
        int ZIndex);
}

public sealed class AudioStateContributor : IPreparedStateContributor, IStateMutationParticipant
{
    private readonly IAudioManager audio;

    public AudioStateContributor(IAudioManager audio)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    public void BindStateMutationGate(StateMutationGate gate)
    {
        if (audio is IStateMutationParticipant participant) participant.BindStateMutationGate(gate);
        else throw new StateCompatibilityException("Audio manager does not implement the persistent write gate contract.");
    }

    public string Key => BuiltInStateKeys.AudioBgm;

    public StateScope Scope => StateScope.Game;

    public int SchemaVersion => 1;

    public StateContributorCapability Capability =>
        StateContributorCapability.DurableRollback;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (audio is not IAudioReplacementSource source)
            throw new StateCompatibilityException("Audio backend cannot freeze committed BGM state.");
        var tracks = source.CaptureCommittedBgm()
            .Select(track => new BgmEntry(track.ClipId, track.TrackId, track.Loop, track.Volume))
            .ToArray();
        return ValueTask.FromResult(new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StateJson.Serialize(new AudioPayload(tracks))));
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
        var entries = Decode(record ?? context.Require(Key));
        if (audio is not IAudioReplacementSource replacement)
            throw new StateCompatibilityException("Audio backend does not support prepared replacement.");
        var prepared = await replacement.PrepareReplacementAsync(entries.Select(track =>
            new BgmRestoreTarget(track.ClipId, track.TrackId, track.Loop, track.Volume)).ToArray(), cancellationToken);
        return new PreparedStateRestore(prepared.Commit, prepared.DisposeAsync);
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken) =>
        await audio.ResetAsync(cancellationToken);

    private BgmEntry[] Decode(StateRecord record)
    {
        var payload = StateJson.Deserialize<AudioPayload>(record);
        var tracks = payload.Tracks ?? Array.Empty<BgmEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(track.ClipId);
            ArgumentException.ThrowIfNullOrWhiteSpace(track.TrackId);
            if (!ids.Add(track.TrackId))
            {
                throw new StateSnapshotException(
                    $"State record '{Key}' contains duplicate BGM track '{track.TrackId}'.");
            }

            if (!float.IsFinite(track.Volume) || track.Volume is < 0 or > 1)
            {
                throw new StateSnapshotException(
                    $"BGM track '{track.TrackId}' has invalid volume '{track.Volume}'.");
            }
        }

        return tracks;
    }

    private sealed record AudioPayload(BgmEntry[] Tracks);

    private sealed record BgmEntry(string ClipId, string TrackId, bool Loop, float Volume);
}

public sealed class ScriptStateContributor : IPreparedStateContributor, ILineBoundaryOwnedStateContributor
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

    internal StateRecord CaptureBoundary(ScriptCheckpoint checkpoint)
    {
        var payload = new ScriptPayload(
            checkpoint.FormatVersion,
            checkpoint.ProgramFingerprint,
            checkpoint.Spot.Script.ProjectId,
            checkpoint.Spot.Script.NodeName,
            checkpoint.Spot.LineId,
            checkpoint.Spot.Boundary,
            checkpoint.ResolvedSubstitutions.ToArray());
        return new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StateJson.Serialize(payload));
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
        var payload = StateJson.Deserialize<ScriptPayload>(record);
        return new ScriptCheckpoint(
            payload.FormatVersion,
            payload.ProgramFingerprint,
            new ScriptPlaybackSpot(
                new ScriptId(payload.ProjectId, payload.NodeName),
                payload.LineId,
                payload.Boundary),
            payload.ResolvedSubstitutions ?? Array.Empty<string>());
    }

    private sealed record ScriptPayload(
        int FormatVersion,
        string ProgramFingerprint,
        string ProjectId,
        string NodeName,
        string LineId,
        ScriptCheckpointBoundary Boundary,
        string[] ResolvedSubstitutions);
}
