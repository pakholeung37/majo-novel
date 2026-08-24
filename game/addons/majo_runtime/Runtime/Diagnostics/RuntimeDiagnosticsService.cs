#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.History;
using Majo.Runtime.Input;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Save;
using Majo.Runtime.State;

namespace Majo.Runtime.Diagnostics;

public sealed class RuntimeDiagnosticsService : IRuntimeDiagnostics
{
    private readonly Func<IServiceResolver> servicesProvider;
    private readonly RingBufferLogSink? ringBuffer;
    private readonly DiagnosticContributorRegistry? contributorRegistry;
    private readonly string runtimeId;
    private readonly string runtimeVersion;
    private bool isInitialized;

    public bool IsInitialized => isInitialized;

    public RuntimeDiagnosticsService(
        IServiceResolver services,
        RingBufferLogSink? ringBuffer = null,
        DiagnosticContributorRegistry? contributorRegistry = null,
        string? runtimeId = null,
        string? runtimeVersion = null)
        : this(
            () => services ?? throw new ArgumentNullException(nameof(services)),
            ringBuffer,
            contributorRegistry,
            runtimeId,
            runtimeVersion)
    {
    }

    public RuntimeDiagnosticsService(
        Func<IServiceResolver> servicesProvider,
        RingBufferLogSink? ringBuffer = null,
        DiagnosticContributorRegistry? contributorRegistry = null,
        string? runtimeId = null,
        string? runtimeVersion = null)
    {
        this.servicesProvider = servicesProvider ?? throw new ArgumentNullException(nameof(servicesProvider));
        this.ringBuffer = ringBuffer;
        this.contributorRegistry = contributorRegistry;
        this.runtimeId = runtimeId ?? Guid.NewGuid().ToString("N");
        this.runtimeVersion = runtimeVersion ?? MajoRuntimeMetadata.PackageVersion;
    }

    public IReadOnlyList<RuntimeLogEvent> RecentEvents =>
        ringBuffer?.RecentEvents ?? Array.Empty<RuntimeLogEvent>();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public RuntimeDiagnosticsSnapshot CaptureSnapshot()
    {
        var services = servicesProvider();
        var hostState = RuntimeHostState.Created;
        RuntimeHost? runtimeHost = null;
        if (services.TryGet<IRuntimeLifetime>(out var lifetime) && lifetime is RuntimeHost host)
        {
            runtimeHost = host;
            hostState = host.State;
        }

        var initializedServicesList = new List<string>();

        DialogueDiagnostics? dialogueDiag = null;
        if (services.TryGet<IDialoguePlayer>(out var dialogue) && dialogue is not null)
        {
            initializedServicesList.Add(nameof(IDialoguePlayer));
            dialogueDiag = new DialogueDiagnostics(
                dialogue.State,
                dialogue.CurrentProjectId,
                dialogue.CurrentNode,
                dialogue.CurrentLineId,
                dialogue.IsPresentingOptions,
                dialogue.CurrentOptions.Count);
        }

        CommandDiagnostics? commandDiag = null;
        if (services.TryGet<IYarnCommandAdapter>(out var commandAdapter) && commandAdapter is not null)
        {
            initializedServicesList.Add(nameof(IYarnCommandAdapter));
            var activeCommands = commandAdapter.ActiveCommands
                .Select(command => new ActiveCommandInfo(
                    command.CommandName,
                    command.ExecutionId,
                    command.SourceLineId,
                    (DateTimeOffset.UtcNow - command.StartedAt).TotalSeconds))
                .ToArray();
            commandDiag = new CommandDiagnostics(
                activeCommands.Length,
                activeCommands,
                commandAdapter.LastFailure?.Message);
        }

        OperationDiagnostics? operationDiag = null;
        if (services.TryGet<IOperationScheduler>(out var operations) && operations is not null)
        {
            initializedServicesList.Add(nameof(IOperationScheduler));
            var activeOps = operations.ActiveOperations.Select(op => new ActiveOperationInfo(
                op.DiagnosticId,
                op.GroupId,
                op.State,
                op.Elapsed.TotalSeconds)).ToArray();
            operationDiag = new OperationDiagnostics(activeOps.Length, activeOps);
        }

        InputDiagnostics? inputDiag = null;
        if (services.TryGet<IInputManager>(out var input) && input is not null)
        {
            initializedServicesList.Add(nameof(IInputManager));
            var handles = input.ActiveHandles;
            var scopes = handles
                .Where(handle => handle.Kind == InputHandleKind.Scope)
                .Select(handle => new InputScopeInfo(
                    handle.Name ?? "<anonymous>",
                    handle.IsModal,
                    handle.Actions))
                .ToArray();
            var modalOwner = handles
                .LastOrDefault(handle =>
                    handle.Kind == InputHandleKind.Scope && handle.IsModal)
                ?.OwnerName;
            inputDiag = new InputDiagnostics(
                input.Enabled,
                input.ActiveHandleCount,
                scopes,
                handles.Count(handle => handle.Kind == InputHandleKind.Muter),
                modalOwner);
        }

        ActorDiagnostics? actorDiag = null;
        if (services.TryGet<IActorManager>(out var actors) && actors is not null)
        {
            initializedServicesList.Add(nameof(IActorManager));
            var actorList = new List<ActiveActorInfo>();
            foreach (var actor in actors.Actors)
            {
                var layer = actor switch
                {
                    ICharacterActor => "character",
                    IBackgroundActor => "background",
                    _ => "custom",
                };
                var zIndex = actor is IGodotActorView { SceneNode: CanvasItem canvasItem }
                    ? canvasItem.ZIndex
                    : 0;
                actorList.Add(new ActiveActorInfo(
                    actor.Id,
                    actor.FactoryId,
                    layer,
                    actor.Appearance,
                    actor.Visible,
                    actor.Position.X,
                    actor.Position.Y,
                    actor.Opacity,
                    zIndex));
            }
            actorDiag = new ActorDiagnostics(actorList);
        }

        AudioDiagnostics? audioDiag = null;
        if (services.TryGet<IAudioManager>(out var audio) && audio is not null)
        {
            initializedServicesList.Add(nameof(IAudioManager));
            var bgmTracks = audio.BgmTracks.Select(t => new BgmTrackInfo(
                t.InstanceId,
                t.ClipId,
                t.State == AudioPlaybackState.Playing,
                t.Volume,
                t.Loop,
                audio.IsFading(t.InstanceId))).ToArray();

            VoicePlaybackInfo? voiceInfo = null;
            if (audio.CurrentVoice is not null)
            {
                voiceInfo = new VoicePlaybackInfo(
                    audio.CurrentVoice.ClipId,
                    audio.CurrentVoice.State == AudioPlaybackState.Playing,
                    audio.CurrentVoice.Volume,
                    audio.IsFading(audio.CurrentVoice.InstanceId));
            }

            audioDiag = new AudioDiagnostics(
                audio.MasterVolume,
                audio.BgmVolume,
                audio.SfxVolume,
                audio.VoiceVolume,
                audio.MasterMuted,
                audio.BgmMuted,
                audio.SfxMuted,
                audio.VoiceMuted,
                audio.VoiceLocale,
                bgmTracks,
                audio.SfxInstances.Count,
                voiceInfo);
        }

        StateDiagnostics? stateDiag = null;
        if (services.TryGet<IStateManager>(out var stateMgr) && stateMgr is not null)
        {
            initializedServicesList.Add(nameof(IStateManager));
            var concreteStateManager = stateMgr as StateManager;
            var contributorsList = concreteStateManager?.RegisteredContributorKeys ??
                stateMgr.CurrentSnapshot?.Records.Keys.ToArray() ??
                Array.Empty<string>();
            string? checkpointLineId = null;
            if (stateMgr.CurrentSnapshot?.Records.TryGetValue(
                    BuiltInStateKeys.ScriptPlayback,
                    out var scriptRecord) == true &&
                scriptRecord.Payload.TryGetProperty("lineId", out var lineIdElement))
            {
                checkpointLineId = lineIdElement.GetString();
            }

            stateDiag = new StateDiagnostics(
                stateMgr.Status,
                StateSnapshot.CurrentFormatVersion,
                stateMgr.CurrentSnapshot is not null,
                checkpointLineId,
                contributorsList,
                concreteStateManager?.LastFailureMessage,
                concreteStateManager?.LastMigrationChain,
                services.TryGet<IStateEligibility>(out var stateEligibility) &&
                    stateEligibility is StateEligibility concreteEligibility
                    ? concreteEligibility.LastResult?.Reasons
                        .Select(value => value.Code.ToString()).ToArray()
                    : null,
                services.TryGet<IStateStorage>(out var stateStorage) &&
                    stateStorage is IStateStorageDiagnostics storageDiagnostics
                    ? storageDiagnostics.LastCommitStage?.ToString()
                    : null,
                stateStorage is IStateStorageDiagnostics storageKeyDiagnostics
                    ? storageKeyDiagnostics.LastKey
                    : null,
                stateStorage is IStateStorageDiagnostics storageFailureDiagnostics
                    ? storageFailureDiagnostics.LastFailureMessage
                    : null);
        }

        PlaybackDiagnostics? playbackDiag = null;
        var modeStr = "Manual";
        var isPaused = false;
        if (services.TryGet<IAutoManager>(out var auto) && auto is not null)
        {
            initializedServicesList.Add(nameof(IAutoManager));
            modeStr = auto.Mode.ToString();
            isPaused = auto.IsPaused;
        }

        var historyIsOpen = false;
        var historyCount = 0;
        var historyCapacity = 0;
        if (services.TryGet<IHistoryManager>(out var history) && history is not null)
        {
            initializedServicesList.Add(nameof(IHistoryManager));
            historyIsOpen = history.IsOpen;
            historyCount = history.Entries.Count;
            historyCapacity = history.Capacity;
        }

        playbackDiag = new PlaybackDiagnostics(
            modeStr,
            isPaused,
            historyIsOpen,
            historyCount,
            historyCapacity,
            modeStr == "Auto",
            auto?.ExtraDelay.TotalSeconds ?? 0,
            auto?.WaitForVoice ?? false);

        SaveSlotDiagnostics? saveSlotDiag = null;
        if (services.TryGet<ISaveSlotManager>(out var saveSlots) && saveSlots is not null)
        {
            initializedServicesList.Add(nameof(ISaveSlotManager));
            var concreteSlots = saveSlots as SaveSlotManager;
            services.TryGet<ISaveProductManager>(out var saveProduct);
            saveSlotDiag = new SaveSlotDiagnostics(
                saveSlots.Status,
                saveSlots.CurrentOperation?.TransactionId,
                saveSlots.CurrentOperation?.Slot.ToString(),
                saveSlots.CurrentOperation?.Phase,
                saveSlots.CurrentSlot?.ToString(),
                concreteSlots?.LastFailurePhase?.ToString(),
                concreteSlots?.LastFailureMessage,
                saveProduct?.PendingAutoSave?.BoundaryId,
                saveProduct?.LastCommittedAutoSaveBoundary);
        }

        MissingResourceDiagnostics? missingResourceDiag = null;
        if (services.TryGet<IMissingResourceReporter>(out var missingReporter) &&
            missingReporter is not null)
        {
            var missing = missingReporter.Occurrences
                .Select(entry => new MissingResourceInfo(
                    entry.ResourceType,
                    entry.ResourceId,
                    entry.Fallback,
                    entry.RequestedPath,
                    entry.Count))
                .ToArray();
            missingResourceDiag = new MissingResourceDiagnostics(
                missing.Length,
                missing.Sum(entry => entry.Count),
                missing);
        }

        var customSections = new Dictionary<string, DiagnosticSection>(StringComparer.Ordinal);
        if (contributorRegistry is not null)
        {
            foreach (var contributor in contributorRegistry.Contributors)
            {
                try
                {
                    customSections[contributor.Key] = contributor.Capture();
                }
                catch
                {
                }
            }
        }

        return new RuntimeDiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            runtimeId,
            runtimeVersion,
            hostState,
            runtimeHost?.InitializedServiceNames ?? initializedServicesList,
            dialogueDiag,
            commandDiag,
            operationDiag,
            inputDiag,
            actorDiag,
            audioDiag,
            stateDiag,
            playbackDiag,
            customSections,
            RecentEvents,
            missingResourceDiag,
            saveSlotDiag);
    }

    public async Task<string> ExportReportAsync(
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = CaptureSnapshot();
        var report = FormatReport(snapshot);

        if (!string.IsNullOrWhiteSpace(destinationPath))
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(destinationPath, report, cancellationToken);
        }

        return report;
    }

    public static string FormatReport(RuntimeDiagnosticsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Majo Runtime Diagnostic Report");
        builder.AppendLine();
        builder.AppendLine($"- **Timestamp**: {snapshot.Timestamp:O}");
        builder.AppendLine($"- **Runtime ID**: `{snapshot.RuntimeId}`");
        builder.AppendLine($"- **Runtime Version**: `{snapshot.RuntimeVersion}`");
        builder.AppendLine($"- **Host State**: `{snapshot.State}`");
        builder.AppendLine($"- **Initialized Services**: {string.Join(", ", snapshot.InitializedServices)}");
        builder.AppendLine();

        if (snapshot.Dialogue is not null)
        {
            builder.AppendLine("## Dialogue");
            builder.AppendLine($"- State: `{snapshot.Dialogue.State}`");
            builder.AppendLine($"- Project ID: `{snapshot.Dialogue.ProjectId ?? "<none>"}`");
            builder.AppendLine($"- Node: `{snapshot.Dialogue.Node ?? "<none>"}`");
            builder.AppendLine($"- Current Line ID: `{snapshot.Dialogue.LineId ?? "<none>"}`");
            builder.AppendLine($"- Is Presenting Options: `{snapshot.Dialogue.IsPresentingOptions}` (Count: {snapshot.Dialogue.OptionCount})");
            builder.AppendLine();
        }

        if (snapshot.Command is not null)
        {
            builder.AppendLine("## Commands");
            builder.AppendLine($"- Active Commands: {snapshot.Command.ActiveCount}");
            if (!string.IsNullOrEmpty(snapshot.Command.LastFailureMessage))
            {
                builder.AppendLine($"- Last Failure: `{snapshot.Command.LastFailureMessage}`");
            }
            builder.AppendLine();
        }

        if (snapshot.Operation is not null)
        {
            builder.AppendLine("## Operations");
            builder.AppendLine($"- Active Operations: {snapshot.Operation.ActiveCount}");
            foreach (var op in snapshot.Operation.ActiveOperations)
            {
                builder.AppendLine($"  - ID: `{op.Id ?? "<anon>"}` (Group: `{op.GroupId ?? "<none>"}`), State: `{op.State}`");
            }
            builder.AppendLine();
        }

        if (snapshot.Input is not null)
        {
            builder.AppendLine("## Input");
            builder.AppendLine($"- Enabled: `{snapshot.Input.Enabled}`");
            builder.AppendLine($"- Active Handles: {snapshot.Input.ActiveHandleCount}");
            builder.AppendLine();
        }

        if (snapshot.Actor is not null)
        {
            builder.AppendLine("## Actors");
            builder.AppendLine($"- Total Registered: {snapshot.Actor.Actors.Count}");
            foreach (var actor in snapshot.Actor.Actors)
            {
                builder.AppendLine($"  - `{actor.Id}` ({actor.FactoryId}, layer: {actor.Layer}, appearance: `{actor.Appearance ?? "none"}`), Visible: `{actor.IsVisible}`, Pos: ({actor.PositionX}, {actor.PositionY}), Alpha: {actor.Alpha:0.##}");
            }
            builder.AppendLine();
        }

        if (snapshot.Audio is not null)
        {
            builder.AppendLine("## Audio");
            builder.AppendLine($"- Volumes: Master={snapshot.Audio.MasterVolume:P0} (Muted: {snapshot.Audio.MasterMuted}), BGM={snapshot.Audio.BgmVolume:P0} (Muted: {snapshot.Audio.BgmMuted}), SFX={snapshot.Audio.SfxVolume:P0} (Muted: {snapshot.Audio.SfxMuted}), Voice={snapshot.Audio.VoiceVolume:P0} (Muted: {snapshot.Audio.VoiceMuted})");
            builder.AppendLine($"- Voice Locale: `{snapshot.Audio.VoiceLocale}`");
            builder.AppendLine($"- Active BGM Tracks: {snapshot.Audio.BgmTracks.Count}");
            foreach (var bgm in snapshot.Audio.BgmTracks)
            {
                builder.AppendLine($"  - `{bgm.TrackId}` -> `{bgm.ClipId}`, Playing: `{bgm.IsPlaying}`, Vol: {bgm.Volume:P0}");
            }
            if (snapshot.Audio.CurrentVoice is not null)
            {
                builder.AppendLine($"- Current Voice: `{snapshot.Audio.CurrentVoice.ClipId}`, Playing: `{snapshot.Audio.CurrentVoice.IsPlaying}`");
            }
            builder.AppendLine();
        }

        if (snapshot.StateDiagnostics is not null)
        {
            builder.AppendLine("## State");
            builder.AppendLine($"- Status: `{snapshot.StateDiagnostics.Status}`");
            builder.AppendLine($"- Format Version: {snapshot.StateDiagnostics.FormatVersion}");
            builder.AppendLine($"- Has Current Snapshot: `{snapshot.StateDiagnostics.HasCurrentSnapshot}`");
            builder.AppendLine($"- Contributors: {string.Join(", ", snapshot.StateDiagnostics.RegisteredContributors)}");
            if (snapshot.StateDiagnostics.LastMigrationChain is { Count: > 0 } migrations)
            {
                builder.AppendLine($"- Migration Chain: {string.Join(" -> ", migrations)}");
            }
            if (snapshot.StateDiagnostics.LastEligibilityReasons is { Count: > 0 } reasons)
            {
                builder.AppendLine($"- Last Eligibility Rejection: {string.Join(", ", reasons)}");
            }
            if (snapshot.StateDiagnostics.StorageCommitStage is not null)
            {
                builder.AppendLine($"- Storage Commit: `{snapshot.StateDiagnostics.StorageCommitStage}` (`{snapshot.StateDiagnostics.StorageKey}`)");
            }
            builder.AppendLine();
        }

        if (snapshot.Playback is not null)
        {
            builder.AppendLine("## Playback");
            builder.AppendLine($"- Mode: `{snapshot.Playback.Mode}` (Paused: `{snapshot.Playback.IsPaused}`)");
            builder.AppendLine($"- History: Open=`{snapshot.Playback.HistoryIsOpen}`, Entries={snapshot.Playback.HistoryItemCount}/{snapshot.Playback.HistoryCapacity}");
            builder.AppendLine();
        }

        if (snapshot.SaveSlots is not null)
        {
            builder.AppendLine("## Save Slots");
            builder.AppendLine($"- Status: `{snapshot.SaveSlots.Status}`");
            builder.AppendLine($"- Current Slot: `{snapshot.SaveSlots.CurrentSlot ?? "<none>"}`");
            if (snapshot.SaveSlots.TransactionId is not null)
            {
                builder.AppendLine(
                    $"- Transaction: `{snapshot.SaveSlots.TransactionId}` " +
                    $"slot=`{snapshot.SaveSlots.Slot}`, phase=`{snapshot.SaveSlots.Phase}`");
            }
            if (snapshot.SaveSlots.LastFailureMessage is not null)
            {
                builder.AppendLine(
                    $"- Last Failure: phase=`{snapshot.SaveSlots.LastFailurePhase}`, " +
                    $"message=`{snapshot.SaveSlots.LastFailureMessage}`");
            }
            builder.AppendLine();
        }

        if (snapshot.MissingResources is { UniqueCount: > 0 } missingResources)
        {
            builder.AppendLine("## Missing Resources");
            builder.AppendLine($"- Unique: {missingResources.UniqueCount}, Total Uses: {missingResources.TotalCount}");
            foreach (var resource in missingResources.Resources)
            {
                builder.AppendLine(
                    $"  - `{resource.ResourceType}` `{resource.ResourceId}` x{resource.Count}; " +
                    $"fallback: `{resource.Fallback}`; path: `{resource.RequestedPath ?? "<none>"}`");
            }
            builder.AppendLine();
        }

        if (snapshot.CustomSections is not null && snapshot.CustomSections.Count > 0)
        {
            builder.AppendLine("## Custom Contributor Sections");
            foreach (var (key, section) in snapshot.CustomSections)
            {
                builder.AppendLine($"### {section.Title} (`{key}`)");
                foreach (var (entryKey, entryVal) in section.Entries)
                {
                    builder.AppendLine($"- {entryKey}: `{entryVal}`");
                }
                builder.AppendLine();
            }
        }

        if (snapshot.RecentLogs is not null && snapshot.RecentLogs.Count > 0)
        {
            builder.AppendLine("## Recent Log Events");
            builder.AppendLine("```text");
            foreach (var log in snapshot.RecentLogs)
            {
                builder.AppendLine(GodotConsoleSink.FormatEvent(log));
            }
            builder.AppendLine("```");
        }

        return builder.ToString();
    }
}
