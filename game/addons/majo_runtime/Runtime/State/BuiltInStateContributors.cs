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
using Majo.Runtime.Script;

namespace Majo.Runtime.State;

public static class BuiltInStateKeys
{
    public const string DialogueVariables = "majo.dialogue.variables";
    public const string Actors = "majo.actors";
    public const string AudioBgm = "majo.audio.bgm";
    public const string ScriptPlayback = "majo.script.playback";
}

public sealed class DialogueVariablesStateContributor : IStateContributor
{
    private readonly IDialogueVariables variables;

    public DialogueVariablesStateContributor(IDialogueVariables variables)
    {
        this.variables = variables ?? throw new ArgumentNullException(nameof(variables));
    }

    public string Key => BuiltInStateKeys.DialogueVariables;

    public StateScope Scope => StateScope.Game;

    public int SchemaVersion => 1;

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

    public ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        variables.ReplaceAll(Decode(record));
        return ValueTask.CompletedTask;
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken) =>
        await variables.ResetAsync(cancellationToken);

    private StateRecord CreateRecord(VariablesPayload payload) =>
        new(Key, Scope, SchemaVersion, StateJson.Serialize(payload));

    private IReadOnlyDictionary<string, DialogueValue> Decode(StateRecord record)
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
            DialogueValueKind.String => DialogueValue.FromString(
                StringValue ?? throw new StateSnapshotException(
                    $"String variable '{Name}' has no value.")),
            DialogueValueKind.Boolean => DialogueValue.FromBoolean(BooleanValue),
            _ => throw new StateSnapshotException(
                $"Variable '{Name}' has unsupported kind '{Kind}'."),
        };
    }
}

public sealed class ActorStateContributor : IStateContributor
{
    private readonly IActorManager actors;

    public ActorStateContributor(IActorManager actors)
    {
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
    }

    public string Key => BuiltInStateKeys.Actors;

    public StateScope Scope => StateScope.Game;

    public int SchemaVersion => 1;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = actors.Actors
            .OrderBy(actor => actor.Id, StringComparer.Ordinal)
            .Select(actor => new ActorEntry(
                actor.Id,
                actor.FactoryId,
                actor.Appearance,
                actor.Visible,
                actor.Position.X,
                actor.Position.Y,
                actor.Tint.R,
                actor.Tint.G,
                actor.Tint.B,
                actor.Opacity))
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

    public async ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
    {
        foreach (var entry in Decode(record))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actor = await actors.CreateAsync(entry.Id, cancellationToken);
            actor.SetAppearance(entry.Appearance);
            actor.SetPosition(new Vector2(entry.PositionX, entry.PositionY));
            actor.SetTint(new Color(entry.TintR, entry.TintG, entry.TintB));
            actor.SetOpacity(entry.Opacity);
            actor.SetVisible(entry.Visible);
        }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken) =>
        await actors.ResetAsync(cancellationToken);

    private IReadOnlyList<ActorEntry> Decode(StateRecord record)
    {
        var payload = StateJson.Deserialize<ActorsPayload>(record);
        var entries = payload.Entries ?? Array.Empty<ActorEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.FactoryId);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Appearance);
            if (!ids.Add(entry.Id))
            {
                throw new StateSnapshotException(
                    $"State record '{Key}' contains duplicate actor '{entry.Id}'.");
            }

            if (!actors.CanCreate(entry.Id, entry.FactoryId))
            {
                throw new StateSnapshotException(
                    $"Actor '{entry.Id}' cannot be restored by factory '{entry.FactoryId}'.");
            }

            if (!float.IsFinite(entry.PositionX) || !float.IsFinite(entry.PositionY) ||
                !float.IsFinite(entry.TintR) || !float.IsFinite(entry.TintG) ||
                !float.IsFinite(entry.TintB) || !float.IsFinite(entry.Opacity) ||
                entry.Opacity is < 0 or > 1)
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
        string FactoryId,
        string Appearance,
        bool Visible,
        float PositionX,
        float PositionY,
        float TintR,
        float TintG,
        float TintB,
        float Opacity);
}

public sealed class AudioStateContributor : IStateContributor
{
    private readonly IAudioManager audio;

    public AudioStateContributor(IAudioManager audio)
    {
        this.audio = audio ?? throw new ArgumentNullException(nameof(audio));
    }

    public string Key => BuiltInStateKeys.AudioBgm;

    public StateScope Scope => StateScope.Game;

    public int SchemaVersion => 1;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tracks = audio.BgmTracks
            .Where(track => track.State == AudioPlaybackState.Playing)
            .OrderBy(track => track.InstanceId, StringComparer.Ordinal)
            .Select(track => new BgmEntry(
                track.ClipId,
                track.InstanceId,
                track.Loop,
                track.BaseVolume))
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

    public async ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken)
    {
        foreach (var track in Decode(record))
        {
            var result = await audio.PlayBgmAsync(
                track.ClipId,
                track.TrackId,
                track.Loop,
                track.Volume,
                cancellationToken: cancellationToken);
            await result.FadeOperation.Completion.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken) =>
        await audio.ResetAsync(cancellationToken);

    private IReadOnlyList<BgmEntry> Decode(StateRecord record)
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

public sealed class ScriptStateContributor : IStateContributor
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

    public int SchemaVersion => 1;

    public IReadOnlyCollection<string> Dependencies => RequiredContributors;

    public ValueTask<StateRecord> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkpoint = playback.CaptureCheckpoint();
        var payload = new ScriptPayload(
            checkpoint.FormatVersion,
            checkpoint.ProgramFingerprint,
            checkpoint.Spot.Script.ProjectId,
            checkpoint.Spot.Script.NodeName,
            checkpoint.Spot.LineId,
            checkpoint.Spot.Boundary,
            checkpoint.ResolvedSubstitutions.ToArray());
        return ValueTask.FromResult(new StateRecord(
            Key,
            Scope,
            SchemaVersion,
            StateJson.Serialize(payload)));
    }

    public async ValueTask PreflightAsync(StateRecord record, CancellationToken cancellationToken) =>
        await playback.ValidateCheckpointAsync(Decode(record), cancellationToken);

    public async ValueTask RestoreAsync(StateRecord record, CancellationToken cancellationToken) =>
        await playback.RestoreCheckpointAsync(Decode(record), cancellationToken);

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
