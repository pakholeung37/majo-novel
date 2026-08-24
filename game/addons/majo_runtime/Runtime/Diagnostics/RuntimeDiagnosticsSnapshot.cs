#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Operations;
using Majo.Runtime.State;

namespace Majo.Runtime.Diagnostics;

public sealed record RuntimeDiagnosticsSnapshot(
    DateTimeOffset Timestamp,
    string RuntimeId,
    string RuntimeVersion,
    RuntimeHostState State,
    IReadOnlyList<string> InitializedServices,
    DialogueDiagnostics? Dialogue = null,
    CommandDiagnostics? Command = null,
    OperationDiagnostics? Operation = null,
    InputDiagnostics? Input = null,
    ActorDiagnostics? Actor = null,
    AudioDiagnostics? Audio = null,
    StateDiagnostics? StateDiagnostics = null,
    PlaybackDiagnostics? Playback = null,
    IReadOnlyDictionary<string, DiagnosticSection>? CustomSections = null,
    IReadOnlyList<RuntimeLogEvent>? RecentLogs = null,
    MissingResourceDiagnostics? MissingResources = null);

public sealed record MissingResourceDiagnostics(
    int UniqueCount,
    int TotalCount,
    IReadOnlyList<MissingResourceInfo> Resources);

public sealed record MissingResourceInfo(
    string ResourceType,
    string ResourceId,
    string Fallback,
    string? RequestedPath,
    int Count);

public sealed record DialogueDiagnostics(
    DialogueSessionState State,
    string? ProjectId,
    string? Node,
    string? LineId,
    bool IsPresentingOptions,
    int OptionCount);

public sealed record CommandDiagnostics(
    int ActiveCount,
    IReadOnlyList<ActiveCommandInfo> ActiveCommands,
    string? LastFailureMessage);

public sealed record ActiveCommandInfo(
    string CommandName,
    string? ExecutionId,
    string? LineId,
    double ElapsedSeconds);

public sealed record OperationDiagnostics(
    int ActiveCount,
    IReadOnlyList<ActiveOperationInfo> ActiveOperations);

public sealed record ActiveOperationInfo(
    string? Id,
    string? GroupId,
    OperationState State,
    double ElapsedSeconds);

public sealed record InputDiagnostics(
    bool Enabled,
    int ActiveHandleCount,
    IReadOnlyList<InputScopeInfo> ActiveScopes,
    int ActiveMuterCount,
    string? ModalOwnerName);

public sealed record InputScopeInfo(
    string Name,
    bool IsModal,
    IReadOnlyList<string> AllowedActions);

public sealed record ActorDiagnostics(
    IReadOnlyList<ActiveActorInfo> Actors);

public sealed record ActiveActorInfo(
    string Id,
    string FactoryId,
    string Layer,
    string? Appearance,
    bool IsVisible,
    float PositionX,
    float PositionY,
    float Alpha,
    int ZIndex);

public sealed record AudioDiagnostics(
    float MasterVolume,
    float BgmVolume,
    float SfxVolume,
    float VoiceVolume,
    bool MasterMuted,
    bool BgmMuted,
    bool SfxMuted,
    bool VoiceMuted,
    string VoiceLocale,
    IReadOnlyList<BgmTrackInfo> BgmTracks,
    int SfxCount,
    VoicePlaybackInfo? CurrentVoice);

public sealed record BgmTrackInfo(
    string TrackId,
    string ClipId,
    bool IsPlaying,
    float Volume,
    bool IsLooping,
    bool IsFading);

public sealed record VoicePlaybackInfo(
    string ClipId,
    bool IsPlaying,
    float Volume,
    bool IsFading);

public sealed record StateDiagnostics(
    StateManagerStatus Status,
    int FormatVersion,
    bool HasCurrentSnapshot,
    string? RecentCheckpointLineId,
    IReadOnlyList<string> RegisteredContributors,
    string? LastFailureMessage = null,
    IReadOnlyList<string>? LastMigrationChain = null,
    IReadOnlyList<string>? LastEligibilityReasons = null,
    string? StorageCommitStage = null,
    string? StorageKey = null,
    string? StorageFailureMessage = null);

public sealed record PlaybackDiagnostics(
    string Mode,
    bool IsPaused,
    bool HistoryIsOpen,
    int HistoryItemCount,
    int HistoryCapacity,
    bool AutoActive,
    double AutoExtraDelaySeconds,
    bool AutoWaitForVoice);
