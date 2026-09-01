#nullable enable

using System;
using System.Collections.Generic;
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public enum YarnHotReloadState
{
    Disabled,
    Watching,
    Debouncing,
    Compiling,
    PendingSafePoint,
    Applying,
    RollingBack,
    Faulted,
}

public enum YarnHotReloadOutcome
{
    Applied,
    ReadyForNextSession,
    NoSourceChange,
    CompileRejected,
    CompatibilityRejected,
    Superseded,
    RolledBack,
    Failed,
}

public sealed record YarnSourceFileSnapshot(
    string LogicalPath,
    string AbsolutePath,
    string Content,
    string ContentHash);

public sealed record YarnSourceSnapshot(
    string ProjectId,
    string ManifestPath,
    int LanguageVersion,
    string BaseLanguage,
    IReadOnlyList<YarnSourceFileSnapshot> Files,
    string SourceFingerprint);

public sealed record YarnCompilationDiagnostic(
    string Severity,
    string? Code,
    string File,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string Message);

public sealed record YarnProjectVersion(
    long Generation,
    string ProjectId,
    string SourceFingerprint,
    string ProgramFingerprint,
    YarnProject Project,
    IReadOnlySet<string> NodeNames,
    IReadOnlySet<string> LineIds,
    IReadOnlyDictionary<string, string> LineNodes,
    IReadOnlySet<string> ImplicitLineIds,
    IReadOnlyList<YarnCompilationDiagnostic> Diagnostics,
    DateTimeOffset CompiledAt)
{
    public IReadOnlyList<NodeDirectionModule> DirectionModules { get; init; } = Array.Empty<NodeDirectionModule>();
}

public sealed record YarnHotReloadStatus(
    YarnHotReloadState State,
    long Generation,
    string ProjectId,
    string? SourceFingerprint,
    string? ProgramFingerprint,
    IReadOnlyList<YarnCompilationDiagnostic> Diagnostics,
    string? Message)
{
    public static YarnHotReloadStatus Disabled { get; } = new(
        YarnHotReloadState.Disabled,
        0,
        string.Empty,
        null,
        null,
        Array.Empty<YarnCompilationDiagnostic>(),
        null);
}

public sealed record YarnHotReloadStatusChanged(
    YarnHotReloadStatus Previous,
    YarnHotReloadStatus Current);

public sealed record YarnHotReloadAttempt(
    string ProjectId,
    long Generation,
    YarnHotReloadOutcome Outcome,
    string? SourceFingerprint,
    string? ProgramFingerprint,
    IReadOnlyList<YarnCompilationDiagnostic> Diagnostics,
    string? Message,
    TimeSpan Elapsed);
