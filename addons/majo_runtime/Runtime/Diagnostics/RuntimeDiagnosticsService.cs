#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
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
using Majo.Runtime.Localization;
using Majo.Runtime.Operations;
using Majo.Runtime.Resources;
using Majo.Runtime.Save;
using Majo.Runtime.Settings;
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
        if (services.TryGet<IRuntimeActivityObserver>(out var activities) && activities is not null)
        {
            initializedServicesList.Add(nameof(IRuntimeActivityObserver));
            var snapshot = activities.Snapshot();
            var activeOps = snapshot.Select(a => new ActiveOperationInfo(
                a.DiagnosticId,
                $"{a.Owner}:{a.Type}",
                OperationState.Running,
                a.Elapsed.TotalSeconds)).ToArray();
            operationDiag = new OperationDiagnostics(activeOps.Length, activeOps);
        }
        else if (services.TryGet<IOperationScheduler>(out var operations) && operations is not null)
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
                actorList.Add(new ActiveActorInfo(
                    actor.Id,
                    actor.FactoryId,
                    layer,
                    actor.Appearance,
                    actor.Visible,
                    actor.Position.X,
                    actor.Position.Y,
                    actor.Opacity,
                    actor.ZIndex,
                    actor is IBackgroundActor background ? background.Slot : null));
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
                t.IsLooping,
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

        var modeStr = "Manual";
        var isPaused = false;
        if (services.TryGet<IPlaybackModeManager>(out var playback) && playback is not null)
        {
            initializedServicesList.Add(nameof(IPlaybackModeManager));
            modeStr = playback.Mode.ToString();
            isPaused = playback.IsPaused;
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

        var playbackDiag = new PlaybackDiagnostics(
            modeStr,
            isPaused,
            historyIsOpen,
            historyCount,
            historyCapacity,
            modeStr == "Auto",
            playback?.ExtraDelay.TotalSeconds ?? 0,
            playback?.WaitForVoice ?? false);

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

        SettingsDiagnostics? settingsDiag = null;
        if (services.TryGet<ISettingsManager>(out var settingsManager) &&
            settingsManager is not null)
        {
            settingsDiag = new SettingsDiagnostics(
                settingsManager.IsDirty,
                settingsManager.Current.DialogueLocale,
                settingsManager.Current.VoiceLocale,
                settingsManager.Current.LettersPerSecond,
                settingsManager.Current.AutoExtraDelaySeconds,
                settingsManager.RecoveryStatus.Kind,
                settingsManager.LastPersistenceError,
                settingsManager.RecoveryStatus.QuarantineKey,
                settingsManager.RecoveryStatus.BackupKey);
        }

        GlobalStateDiagnostics? globalDiag = null;
        if (services.TryGet<IGlobalStateManager>(out var globalManager) &&
            globalManager is not null)
        {
            globalDiag = new GlobalStateDiagnostics(
                globalManager.IsDirty,
                globalManager.RecoveryStatus.Kind,
                globalManager.LastPersistenceError,
                globalManager.RecoveryStatus.QuarantineKey,
                globalManager.RecoveryStatus.BackupKey);
        }

        LocaleDiagnostics? localeDiag = null;
        if (services.TryGet<ILocaleManager>(out var localeManager) &&
            localeManager is not null)
        {
            initializedServicesList.Add(nameof(ILocaleManager));
            var diag = localeManager.CaptureDiagnostics();
            localeDiag = new LocaleDiagnostics(
                diag.DialogueLocale,
                diag.VoiceLocale,
                diag.DefaultLocale,
                diag.DefaultVoiceLocale,
                diag.AvailableLocales,
                diag.DialogueFallbackChain,
                diag.VoiceFallbackChain,
                diag.SwitchState.ToString(),
                diag.MissingKeyCount);
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
            saveSlotDiag,
            settingsDiag,
            globalDiag,
            localeDiag);
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
        builder.AppendLine(CultureInfo.InvariantCulture, $"- **Timestamp**: {snapshot.Timestamp:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- **Runtime ID**: `{snapshot.RuntimeId}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- **Runtime Version**: `{snapshot.RuntimeVersion}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- **Host State**: `{snapshot.State}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- **Initialized Services**: {string.Join(", ", snapshot.InitializedServices)}");
        builder.AppendLine();

        if (snapshot.Dialogue is not null)
        {
            builder.AppendLine("## Dialogue");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- State: `{snapshot.Dialogue.State}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Project ID: `{snapshot.Dialogue.ProjectId ?? "<none>"}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Node: `{snapshot.Dialogue.Node ?? "<none>"}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Current Line ID: `{snapshot.Dialogue.LineId ?? "<none>"}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Is Presenting Options: `{snapshot.Dialogue.IsPresentingOptions}` (Count: {snapshot.Dialogue.OptionCount})");
            builder.AppendLine();
        }

        if (snapshot.Command is not null)
        {
            builder.AppendLine("## Commands");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Active Commands: {snapshot.Command.ActiveCount}");
            if (!string.IsNullOrEmpty(snapshot.Command.LastFailureMessage))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Last Failure: `{snapshot.Command.LastFailureMessage}`");
            }
            builder.AppendLine();
        }

        if (snapshot.Operation is not null)
        {
            builder.AppendLine("## Operations");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Active Operations: {snapshot.Operation.ActiveCount}");
            foreach (var op in snapshot.Operation.ActiveOperations)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - ID: `{op.Id ?? "<anon>"}` (Group: `{op.GroupId ?? "<none>"}`), State: `{op.State}`");
            }
            builder.AppendLine();
        }

        if (snapshot.Input is not null)
        {
            builder.AppendLine("## Input");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Enabled: `{snapshot.Input.Enabled}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Active Handles: {snapshot.Input.ActiveHandleCount}");
            builder.AppendLine();
        }

        if (snapshot.Actor is not null)
        {
            builder.AppendLine("## Actors");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Total Registered: {snapshot.Actor.Actors.Count}");
            foreach (var actor in snapshot.Actor.Actors)
            {
                var slot = actor.Slot is null ? string.Empty : $", slot: {actor.Slot}";
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - `{actor.Id}` ({actor.FactoryId}, layer: {actor.Layer}{slot}, appearance: `{actor.Appearance ?? "none"}`), Visible: `{actor.IsVisible}`, Pos: ({actor.PositionX}, {actor.PositionY}), Alpha: {actor.Alpha:0.##}");
            }
            builder.AppendLine();
        }

        if (snapshot.Audio is not null)
        {
            builder.AppendLine("## Audio");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Volumes: Master={snapshot.Audio.MasterVolume:P0} (Muted: {snapshot.Audio.MasterMuted}), BGM={snapshot.Audio.BgmVolume:P0} (Muted: {snapshot.Audio.BgmMuted}), SFX={snapshot.Audio.SfxVolume:P0} (Muted: {snapshot.Audio.SfxMuted}), Voice={snapshot.Audio.VoiceVolume:P0} (Muted: {snapshot.Audio.VoiceMuted})");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Voice Locale: `{snapshot.Audio.VoiceLocale}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Active BGM Tracks: {snapshot.Audio.BgmTracks.Count}");
            foreach (var bgm in snapshot.Audio.BgmTracks)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - `{bgm.TrackId}` -> `{bgm.ClipId}`, Playing: `{bgm.IsPlaying}`, Vol: {bgm.Volume:P0}");
            }
            if (snapshot.Audio.CurrentVoice is not null)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Current Voice: `{snapshot.Audio.CurrentVoice.ClipId}`, Playing: `{snapshot.Audio.CurrentVoice.IsPlaying}`");
            }
            builder.AppendLine();
        }

        if (snapshot.StateDiagnostics is not null)
        {
            builder.AppendLine("## State");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Status: `{snapshot.StateDiagnostics.Status}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Format Version: {snapshot.StateDiagnostics.FormatVersion}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Has Current Snapshot: `{snapshot.StateDiagnostics.HasCurrentSnapshot}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Contributors: {string.Join(", ", snapshot.StateDiagnostics.RegisteredContributors)}");
            if (snapshot.StateDiagnostics.LastMigrationChain is { Count: > 0 } migrations)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Migration Chain: {string.Join(" -> ", migrations)}");
            }
            if (snapshot.StateDiagnostics.LastEligibilityReasons is { Count: > 0 } reasons)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Last Eligibility Rejection: {string.Join(", ", reasons)}");
            }
            if (snapshot.StateDiagnostics.StorageCommitStage is not null)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Storage Commit: `{snapshot.StateDiagnostics.StorageCommitStage}` (`{snapshot.StateDiagnostics.StorageKey}`)");
            }
            builder.AppendLine();
        }

        if (snapshot.Playback is not null)
        {
            builder.AppendLine("## Playback");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Mode: `{snapshot.Playback.Mode}` (Paused: `{snapshot.Playback.IsPaused}`)");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- History: Open=`{snapshot.Playback.HistoryIsOpen}`, Entries={snapshot.Playback.HistoryItemCount}/{snapshot.Playback.HistoryCapacity}");
            builder.AppendLine();
        }

        if (snapshot.SaveSlots is not null)
        {
            builder.AppendLine("## Save Slots");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Status: `{snapshot.SaveSlots.Status}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Current Slot: `{snapshot.SaveSlots.CurrentSlot ?? "<none>"}`");
            if (snapshot.SaveSlots.TransactionId is not null)
            {
                builder.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- Transaction: `{snapshot.SaveSlots.TransactionId}` " +
                    $"slot=`{snapshot.SaveSlots.Slot}`, phase=`{snapshot.SaveSlots.Phase}`");
            }
            if (snapshot.SaveSlots.LastFailureMessage is not null)
            {
                builder.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- Last Failure: phase=`{snapshot.SaveSlots.LastFailurePhase}`, " +
                    $"message=`{snapshot.SaveSlots.LastFailureMessage}`");
            }
            builder.AppendLine();
        }

        if (snapshot.Locale is not null)
        {
            builder.AppendLine("## Localization");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Dialogue Locale: `{snapshot.Locale.DialogueLocale}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Voice Locale: `{snapshot.Locale.VoiceLocale}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Default Dialogue Locale: `{snapshot.Locale.DefaultLocale}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Default Voice Locale: `{snapshot.Locale.DefaultVoiceLocale}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Available Locales: {string.Join(", ", snapshot.Locale.AvailableLocales)}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Dialogue Fallback: {string.Join(" -> ", snapshot.Locale.DialogueFallbackChain)}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Voice Fallback: {string.Join(" -> ", snapshot.Locale.VoiceFallbackChain)}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Switch State: `{snapshot.Locale.SwitchState}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Missing Keys: {snapshot.Locale.MissingKeyCount}");
            builder.AppendLine();
        }

        if (snapshot.MissingResources is { UniqueCount: > 0 } missingResources)
        {
            builder.AppendLine("## Missing Resources");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Unique: {missingResources.UniqueCount}, Total Uses: {missingResources.TotalCount}");
            foreach (var resource in missingResources.Resources)
            {
                builder.AppendLine(
                    CultureInfo.InvariantCulture,
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
                builder.AppendLine(CultureInfo.InvariantCulture, $"### {section.Title} (`{key}`)");
                foreach (var (entryKey, entryVal) in section.Entries)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"- {entryKey}: `{entryVal}`");
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
