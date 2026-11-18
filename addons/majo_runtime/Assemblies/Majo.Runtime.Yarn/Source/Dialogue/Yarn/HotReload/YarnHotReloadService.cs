#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using static Majo.Runtime.Dialogue.Yarn.HotReload.YarnHotReloadLog;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public sealed class YarnHotReloadService : IYarnHotReloadService, IDisposable
{
    private const string LogCategory = "script.hot_reload";
    private readonly IYarnProjectVersionStore versions;
    private readonly IScriptPlayback script;
    private readonly YarnHotReloadConfiguration configuration;
    private readonly string defaultProjectId;
    private readonly string scriptsRoot;
    private readonly bool sourceFilesAvailable;
    private readonly YarnSourceSnapshotReader snapshotReader;
    private readonly YarnRuntimeCompiler compiler;
    private readonly YarnProjectMaterializer materializer;
    private readonly IPlaybackModeManager? playback;
    private readonly IRuntimeLogger logger;
    private readonly YarnHotReloadApplier applier;
    private readonly object sync = new();
    private readonly SemaphoreSlim compileGate = new(1, 1);
    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? pendingReload;
    private YarnSourceMonitor? sourceMonitor;
    private YarnHotReloadDispatcher? dispatcher;
    private long requestedGeneration;
    private bool enabled;
    private readonly Dictionary<string, YarnProjectVersion> pendingCandidates =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lastRejectedSourceFingerprints =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim applyGate = new(1, 1);
    private CancellationTokenSource? resetCancellation;

    public YarnHotReloadService(
        IYarnProjectVersionStore versions,
        IScriptPlayback script,
        IYarnDialogueBackend backend,
        YarnHotReloadConfiguration configuration,
        string defaultProjectId,
        string scriptsRoot,
        IRuntimeLogger? logger = null,
        YarnSourceSnapshotReader? snapshotReader = null,
        YarnRuntimeCompiler? compiler = null,
        YarnProjectMaterializer? materializer = null,
        IPlaybackModeManager? playback = null,
        IStateManager? state = null,
        bool sourceFilesAvailable = true)
    {
        this.versions = versions ?? throw new ArgumentNullException(nameof(versions));
        this.script = script ?? throw new ArgumentNullException(nameof(script));
        ArgumentNullException.ThrowIfNull(backend);
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.defaultProjectId = RequireId(defaultProjectId, nameof(defaultProjectId));
        this.sourceFilesAvailable = sourceFilesAvailable;
        this.scriptsRoot = sourceFilesAvailable
            ? Path.GetFullPath(RequireId(scriptsRoot, nameof(scriptsRoot))) : string.Empty;
        this.logger = logger ?? NullRuntimeLogger.Instance;
        this.snapshotReader = snapshotReader ?? new YarnSourceSnapshotReader();
        this.compiler = compiler ?? new YarnRuntimeCompiler();
        this.materializer = materializer ?? new YarnProjectMaterializer();
        this.playback = playback;
        applier = new YarnHotReloadApplier(versions, script, backend, configuration, playback, state, this.logger);
    }

    public event Action<YarnHotReloadStatusChanged>? StatusChanged;

    public event Action<YarnHotReloadAttempt>? AttemptCompleted;

    public YarnHotReloadStatus Status { get; private set; } = YarnHotReloadStatus.Disabled;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dispatcher = new YarnHotReloadDispatcher(SynchronizationContext.Current);
        enabled = configuration.Enabled && sourceFilesAvailable && OS.IsDebugBuild() &&
            (OS.HasFeature("editor") || configuration.EnableInExportedDebugBuild);
        if (!enabled)
        {
            if (configuration.Enabled)
            {
                logger.Info(
                    LogCategory,
                    "script.hot_reload.disabled_by_build",
                    "Yarn hot reload is disabled for this build.");
            }
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(scriptsRoot);
        lifetime = new CancellationTokenSource();
        resetCancellation = new CancellationTokenSource();
        script.PhaseChanged += HandlePhaseChanged;
        if (playback is not null)
        {
            playback.PauseChanged += HandlePlaybackPauseChanged;
        }
        sourceMonitor = new YarnSourceMonitor(scriptsRoot, configuration.ConsistencyScanMilliseconds,
            async token => (await YarnSourceSnapshotReader.ReadAsync(defaultProjectId,
                ResolveManifestPath(defaultProjectId), token)).SourceFingerprint,
            fingerprint => versions.UpdateSourceFingerprint(defaultProjectId, fingerprint),
            () => QueueReload(defaultProjectId), logger);
        SetStatus(new YarnHotReloadStatus(
            YarnHotReloadState.Watching,
            0,
            defaultProjectId,
            null,
            null,
            Array.Empty<YarnCompilationDiagnostic>(),
            null));
        logger.Info(
            LogCategory,
            "script.hot_reload.watching_started",
            $"Watching Yarn sources for project '{defaultProjectId}'.");
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ResetCoreAsync(cancellationToken);
    }

    private async Task ResetCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requestedGeneration);
        CancellationTokenSource? pendingToDispose;
        CancellationTokenSource? resetToCancel;
        lock (sync)
        {
            pendingToDispose = pendingReload;
            pendingReload = null;
            resetToCancel = resetCancellation;
            resetCancellation = new CancellationTokenSource();
            pendingCandidates.Clear();
            lastRejectedSourceFingerprints.Clear();
        }
        sourceMonitor?.ResetBaseline();
        pendingToDispose?.Cancel();
        pendingToDispose?.Dispose();
        resetToCancel?.Cancel();
        resetToCancel?.Dispose();
        await compileGate.WaitAsync(cancellationToken);
        compileGate.Release();
        await applyGate.WaitAsync(cancellationToken);
        applyGate.Release();
        if (enabled)
        {
            SetWatching(
                Volatile.Read(ref requestedGeneration),
                defaultProjectId,
                null,
                null,
                Array.Empty<YarnCompilationDiagnostic>(),
                null);
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (sourceMonitor is not null) await sourceMonitor.DisposeAsync();
        sourceMonitor = null;
        script.PhaseChanged -= HandlePhaseChanged;
        if (playback is not null)
        {
            playback.PauseChanged -= HandlePlaybackPauseChanged;
        }
        CancellationTokenSource? lifetimeToDispose;
        CancellationTokenSource? pendingToDispose;
        CancellationTokenSource? resetToDispose;
        lock (sync)
        {
            lifetimeToDispose = lifetime;
            pendingToDispose = pendingReload;
            lifetime = null;
            pendingReload = null;
            resetToDispose = resetCancellation;
            resetCancellation = null;
        }
        lifetimeToDispose?.Cancel();
        pendingToDispose?.Cancel();
        resetToDispose?.Cancel();
        await compileGate.WaitAsync(cancellationToken);
        compileGate.Release();
        await applyGate.WaitAsync(cancellationToken);
        applyGate.Release();
        lifetimeToDispose?.Dispose();
        pendingToDispose?.Dispose();
        resetToDispose?.Dispose();
        enabled = false;
        SetStatus(YarnHotReloadStatus.Disabled);
    }

    public async Task RequestReloadAsync(
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            throw new InvalidOperationException("Yarn hot reload is not enabled.");
        }

        var targetProject = RequireId(projectId ?? defaultProjectId, nameof(projectId));
        var generation = Interlocked.Increment(ref requestedGeneration);
        await CompileGenerationAsync(targetProject, generation, cancellationToken);
    }

    private void QueueReload(string projectId)
    {
        var currentLifetime = lifetime;
        if (!enabled || currentLifetime?.IsCancellationRequested != false)
        {
            return;
        }

        var generation = Interlocked.Increment(ref requestedGeneration);
        CancellationTokenSource current;
        lock (sync)
        {
            if (!enabled || !ReferenceEquals(lifetime, currentLifetime) ||
                currentLifetime.IsCancellationRequested)
                return;
            pendingReload?.Cancel();
            pendingReload?.Dispose();
            pendingReload = CancellationTokenSource.CreateLinkedTokenSource(currentLifetime.Token);
            current = pendingReload;
        }
        SetStatus(new YarnHotReloadStatus(
            YarnHotReloadState.Debouncing,
            generation,
            projectId,
            Status.SourceFingerprint,
            Status.ProgramFingerprint,
            Status.Diagnostics,
            null));
        _ = DebounceAndCompileAsync(projectId, generation, current.Token);
    }

    private async Task DebounceAndCompileAsync(
        string projectId,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(configuration.DebounceMilliseconds, cancellationToken);
            await CompileGenerationAsync(projectId, generation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishFailure(projectId, generation, exception);
        }
    }

    private async Task CompileGenerationAsync(
        string projectId,
        long generation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await compileGate.WaitAsync(cancellationToken);
        try
        {
            if (generation != Volatile.Read(ref requestedGeneration))
            {
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.Superseded,
                    null, null, Array.Empty<YarnCompilationDiagnostic>(), null, stopwatch.Elapsed);
                return;
            }

            SetStatus(new YarnHotReloadStatus(
                YarnHotReloadState.Compiling,
                generation,
                projectId,
                Status.SourceFingerprint,
                Status.ProgramFingerprint,
                Array.Empty<YarnCompilationDiagnostic>(),
                null));
            var snapshot = await YarnSourceSnapshotReader.ReadAsync(
                projectId,
                ResolveManifestPath(projectId),
                cancellationToken);
            if (string.Equals(projectId, defaultProjectId, StringComparison.Ordinal))
                sourceMonitor?.AcceptFingerprint(snapshot.SourceFingerprint);
            var active = versions.GetActiveVersion(projectId);
            if (active is not null && string.Equals(
                active.SourceFingerprint,
                snapshot.SourceFingerprint,
                StringComparison.Ordinal))
            {
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.NoSourceChange,
                    snapshot.SourceFingerprint, active.ProgramFingerprint,
                    Array.Empty<YarnCompilationDiagnostic>(), null, stopwatch.Elapsed);
                SetWatching(generation, projectId, snapshot.SourceFingerprint,
                    active.ProgramFingerprint, Array.Empty<YarnCompilationDiagnostic>(), null);
                return;
            }
            string? rejectedFingerprint;
            lock (sync)
            {
                lastRejectedSourceFingerprints.TryGetValue(projectId, out rejectedFingerprint);
            }
            if (string.Equals(rejectedFingerprint, snapshot.SourceFingerprint,
                StringComparison.Ordinal))
            {
                SetWatching(generation, projectId, snapshot.SourceFingerprint,
                    Status.ProgramFingerprint, Status.Diagnostics, Status.Message);
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.NoSourceChange,
                    snapshot.SourceFingerprint, Status.ProgramFingerprint,
                    Status.Diagnostics, Status.Message, stopwatch.Elapsed);
                return;
            }

            var compilation = await compiler.CompileAsync(snapshot, generation, cancellationToken);
            if (generation != Volatile.Read(ref requestedGeneration))
            {
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.Superseded,
                    snapshot.SourceFingerprint, null, compilation.Diagnostics, null, stopwatch.Elapsed);
                return;
            }

            if (compilation.HasErrors || compilation.Result.Program is null)
            {
                lock (sync)
                {
                    lastRejectedSourceFingerprints[projectId] = snapshot.SourceFingerprint;
                }
                var message = "Yarn compilation failed; the last-known-good project remains active.";
                SetWatching(generation, projectId, snapshot.SourceFingerprint, null,
                    compilation.Diagnostics, message);
                logger.Error(LogCategory, "script.hot_reload.compile_failed", message,
                    properties: Properties(projectId, generation, snapshot.SourceFingerprint, null,
                        YarnHotReloadOutcome.CompileRejected));
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.CompileRejected,
                    snapshot.SourceFingerprint, null, compilation.Diagnostics, message, stopwatch.Elapsed);
                return;
            }

            var version = await dispatcher!.InvokeAsync(
                () => YarnProjectMaterializer.Materialize(generation, compilation),
                cancellationToken);
            lock (sync)
            {
                lastRejectedSourceFingerprints.Remove(projectId);
            }
            if (generation != Volatile.Read(ref requestedGeneration))
            {
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.Superseded,
                    version.SourceFingerprint, version.ProgramFingerprint,
                    version.Diagnostics, null, stopwatch.Elapsed);
                return;
            }

            var currentActive = versions.GetActiveVersion(projectId) ?? active;
            if (currentActive is not null && YarnHotReloadComparison.IsContentEquivalent(currentActive, version))
            {
                versions.UpdateSourceFingerprint(projectId, version.SourceFingerprint);
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.NoSourceChange,
                    version.SourceFingerprint, version.ProgramFingerprint,
                    version.Diagnostics, null, stopwatch.Elapsed);
                SetWatching(generation, projectId, version.SourceFingerprint,
                    version.ProgramFingerprint, Array.Empty<YarnCompilationDiagnostic>(), null);
                return;
            }

            versions.StageForNextSession(version);
            if (applier.IsAtSafePoint)
            {
                await ApplyCandidateAsync(version, cancellationToken);
                return;
            }

            if (script.Phase is not ScriptPlaybackPhase.Idle and not ScriptPlaybackPhase.Faulted)
            {
                lock (sync)
                {
                    pendingCandidates[projectId] = version;
                }
                var pending = "Yarn candidate is waiting for an awaiting-advance boundary.";
                SetStatus(new YarnHotReloadStatus(
                    YarnHotReloadState.PendingSafePoint,
                    generation,
                    projectId,
                    version.SourceFingerprint,
                    version.ProgramFingerprint,
                    version.Diagnostics,
                    pending));
                logger.Info(LogCategory, "script.hot_reload.pending_safe_point", pending,
                    properties: Properties(projectId, generation, version.SourceFingerprint,
                        version.ProgramFingerprint, YarnHotReloadOutcome.ReadyForNextSession));
                return;
            }

            var success = "Yarn candidate compiled and will be used by the next dialogue session.";
            SetWatching(generation, projectId, version.SourceFingerprint,
                version.ProgramFingerprint, version.Diagnostics, success);
            logger.Info(LogCategory, "script.hot_reload.compile_succeeded", success,
                properties: Properties(projectId, generation, version.SourceFingerprint,
                    version.ProgramFingerprint, YarnHotReloadOutcome.ReadyForNextSession));
            PublishAttempt(projectId, generation, YarnHotReloadOutcome.ReadyForNextSession,
                version.SourceFingerprint, version.ProgramFingerprint,
                version.Diagnostics, success, stopwatch.Elapsed);
        }
        finally
        {
            compileGate.Release();
        }
    }

    private void HandlePhaseChanged(ScriptPlaybackPhaseChanged change)
    {
        if (change.Current != ScriptPlaybackPhase.AwaitingAdvance || !enabled)
        {
            return;
        }

        TryApplyPendingCandidate();
    }

    private void HandlePlaybackPauseChanged(PlaybackPauseStateChanged change)
    {
        if (!change.IsPaused && enabled)
        {
            TryApplyPendingCandidate();
        }
    }

    private void TryApplyPendingCandidate()
    {
        if (!applier.IsAtSafePoint)
        {
            return;
        }

        YarnProjectVersion? candidate;
        lock (sync)
        {
            candidate = pendingCandidates.Values
                .OrderByDescending(value => value.Generation)
                .FirstOrDefault();
            if (candidate is not null)
            {
                pendingCandidates.Remove(candidate.ProjectId);
            }
        }
        var currentLifetime = lifetime;
        if (candidate is not null && currentLifetime?.IsCancellationRequested == false)
        {
            _ = ApplyCandidateSafelyAsync(candidate, currentLifetime.Token);
        }
    }

    private async Task ApplyCandidateSafelyAsync(
        YarnProjectVersion candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            await ApplyCandidateAsync(candidate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishFailure(candidate.ProjectId, candidate.Generation, exception);
        }
    }

    private async Task ApplyCandidateAsync(
        YarnProjectVersion candidate,
        CancellationToken cancellationToken)
    {
        await applyGate.WaitAsync(cancellationToken);
        try
        {
            await dispatcher!.InvokeAsync(
                () => ApplyCandidateOnMainThreadAsync(candidate, cancellationToken),
                cancellationToken);
        }
        finally
        {
            applyGate.Release();
        }
    }

    private async Task ApplyCandidateOnMainThreadAsync(
        YarnProjectVersion candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.Generation != Volatile.Read(ref requestedGeneration))
        {
            return;
        }
        if (!applier.IsAtSafePoint)
        {
            lock (sync)
            {
                if (candidate.Generation == Volatile.Read(ref requestedGeneration))
                {
                    pendingCandidates[candidate.ProjectId] = candidate;
                }
            }
            return;
        }

        var result = await applier.ApplyAsync(candidate, phase =>
        {
            if (phase == YarnHotReloadState.Applying)
                SetStatus(new YarnHotReloadStatus(phase, candidate.Generation, candidate.ProjectId,
                    candidate.SourceFingerprint, candidate.ProgramFingerprint, candidate.Diagnostics, null));
            else SetStatus(Status with { State = phase });
        }, cancellationToken);
        SetWatching(result.Generation, result.ProjectId, result.SourceFingerprint,
            result.ProgramFingerprint, result.Diagnostics, result.Message);
        PublishAttempt(result.ProjectId, result.Generation, result.Outcome, result.SourceFingerprint,
            result.ProgramFingerprint, result.Diagnostics, result.Message, result.Elapsed);
    }

    private string ResolveManifestPath(string projectId)
    {
        if (projectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            projectId.Contains(Path.DirectorySeparatorChar) ||
            projectId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Yarn project ID must be a file name.", nameof(projectId));
        }
        return Path.Combine(scriptsRoot, projectId + YarnSpinnerGodot.YarnProject.YARN_PROJECT_EXTENSION);
    }

    private void PublishFailure(string projectId, long generation, Exception exception)
    {
        var message = $"Yarn hot reload failed: {exception.Message}";
        SetWatching(generation, projectId, null, null,
            Array.Empty<YarnCompilationDiagnostic>(), message);
        logger.Error(LogCategory, "script.hot_reload.failed", message, exception,
            properties: Properties(projectId, generation, null, null, YarnHotReloadOutcome.Failed));
        PublishAttempt(projectId, generation, YarnHotReloadOutcome.Failed,
            null, null, Array.Empty<YarnCompilationDiagnostic>(), message, TimeSpan.Zero);
    }

    private void SetWatching(
        long generation,
        string projectId,
        string? sourceFingerprint,
        string? programFingerprint,
        IReadOnlyList<YarnCompilationDiagnostic> diagnostics,
        string? message) => SetStatus(new YarnHotReloadStatus(
            YarnHotReloadState.Watching,
            generation,
            projectId,
            sourceFingerprint,
            programFingerprint,
            diagnostics,
            message));

    private void SetStatus(YarnHotReloadStatus status)
    {
        YarnHotReloadStatus previous;
        lock (sync)
        {
            previous = Status;
            Status = status;
        }
        StatusChanged?.Invoke(new YarnHotReloadStatusChanged(previous, status));
    }

    private void PublishAttempt(
        string projectId,
        long generation,
        YarnHotReloadOutcome outcome,
        string? sourceFingerprint,
        string? programFingerprint,
        IReadOnlyList<YarnCompilationDiagnostic> diagnostics,
        string? message,
        TimeSpan elapsed) => AttemptCompleted?.Invoke(new YarnHotReloadAttempt(
            projectId,
            generation,
            outcome,
            sourceFingerprint,
            programFingerprint,
            diagnostics,
            message,
            elapsed));

    private static string RequireId(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        return value.Trim();
    }

    public void Dispose()
    {
        compileGate.Dispose();
        applyGate.Dispose();
        lifetime?.Dispose();
        pendingReload?.Dispose();
        sourceMonitor?.Dispose();
        resetCancellation?.Dispose();
    }
}
