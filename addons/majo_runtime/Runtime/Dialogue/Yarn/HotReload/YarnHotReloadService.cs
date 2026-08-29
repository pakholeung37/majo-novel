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
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public sealed class YarnHotReloadService : IYarnHotReloadService
{
    private const string LogCategory = "script.hot_reload";
    private readonly IYarnProjectVersionStore versions;
    private readonly IScriptPlayback script;
    private readonly IYarnDialogueBackend backend;
    private readonly YarnHotReloadConfiguration configuration;
    private readonly string defaultProjectId;
    private readonly string scriptsRoot;
    private readonly YarnSourceSnapshotReader snapshotReader;
    private readonly YarnRuntimeCompiler compiler;
    private readonly YarnProjectMaterializer materializer;
    private readonly IPlaybackModeManager? playback;
    private readonly IRuntimeLogger logger;
    private readonly object sync = new();
    private readonly SemaphoreSlim compileGate = new(1, 1);
    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? pendingReload;
    private FileSystemWatcher? watcher;
    private System.Threading.Timer? consistencyTimer;
    private SynchronizationContext? mainContext;
    private long requestedGeneration;
    private bool enabled;
    private YarnProjectVersion? pendingCandidate;
    private string? lastRejectedSourceFingerprint;

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
        IPlaybackModeManager? playback = null)
    {
        this.versions = versions ?? throw new ArgumentNullException(nameof(versions));
        this.script = script ?? throw new ArgumentNullException(nameof(script));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.defaultProjectId = RequireId(defaultProjectId, nameof(defaultProjectId));
        this.scriptsRoot = Path.GetFullPath(
            RequireId(scriptsRoot, nameof(scriptsRoot)));
        this.logger = logger ?? NullRuntimeLogger.Instance;
        this.snapshotReader = snapshotReader ?? new YarnSourceSnapshotReader();
        this.compiler = compiler ?? new YarnRuntimeCompiler();
        this.materializer = materializer ?? new YarnProjectMaterializer();
        this.playback = playback;
    }

    public event Action<YarnHotReloadStatusChanged>? StatusChanged;

    public event Action<YarnHotReloadAttempt>? AttemptCompleted;

    public YarnHotReloadStatus Status { get; private set; } = YarnHotReloadStatus.Disabled;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mainContext = SynchronizationContext.Current;
        enabled = configuration.Enabled && OS.IsDebugBuild() &&
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
        script.PhaseChanged += HandlePhaseChanged;
        if (playback is not null)
        {
            playback.PauseChanged += HandlePlaybackPauseChanged;
        }
        watcher = new FileSystemWatcher(scriptsRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += HandleSourceEvent;
        watcher.Created += HandleSourceEvent;
        watcher.Deleted += HandleSourceEvent;
        watcher.Renamed += HandleSourceEvent;
        watcher.Error += HandleWatcherError;
        consistencyTimer = new System.Threading.Timer(
            _ => QueueReload(defaultProjectId),
            null,
            configuration.ConsistencyScanMilliseconds,
            configuration.ConsistencyScanMilliseconds);
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
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        watcher?.Dispose();
        watcher = null;
        script.PhaseChanged -= HandlePhaseChanged;
        if (playback is not null)
        {
            playback.PauseChanged -= HandlePlaybackPauseChanged;
        }
        await (consistencyTimer?.DisposeAsync() ?? ValueTask.CompletedTask);
        consistencyTimer = null;
        CancellationTokenSource? lifetimeToDispose;
        CancellationTokenSource? pendingToDispose;
        lock (sync)
        {
            lifetimeToDispose = lifetime;
            pendingToDispose = pendingReload;
            lifetime = null;
            pendingReload = null;
        }
        lifetimeToDispose?.Cancel();
        pendingToDispose?.Cancel();
        await compileGate.WaitAsync(cancellationToken);
        compileGate.Release();
        lifetimeToDispose?.Dispose();
        pendingToDispose?.Dispose();
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

    private void HandleSourceEvent(object sender, FileSystemEventArgs args)
    {
        if (IsYarnPath(args.FullPath))
        {
            QueueReload(defaultProjectId);
        }
    }

    private void HandleWatcherError(object sender, ErrorEventArgs args)
    {
        logger.Warning(
            LogCategory,
            "script.hot_reload.watcher_overflow",
            "The Yarn source watcher lost events; a consistency scan was scheduled.",
            args.GetException());
        QueueReload(defaultProjectId);
    }

    private void QueueReload(string projectId)
    {
        if (!enabled || lifetime?.IsCancellationRequested != false)
        {
            return;
        }

        var generation = Interlocked.Increment(ref requestedGeneration);
        CancellationTokenSource current;
        lock (sync)
        {
            pendingReload?.Cancel();
            pendingReload?.Dispose();
            pendingReload = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
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
            var snapshot = await snapshotReader.ReadAsync(
                projectId,
                ResolveManifestPath(projectId),
                cancellationToken);
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
            if (string.Equals(
                lastRejectedSourceFingerprint,
                snapshot.SourceFingerprint,
                StringComparison.Ordinal))
            {
                SetWatching(generation, projectId, snapshot.SourceFingerprint,
                    Status.ProgramFingerprint, Status.Diagnostics, Status.Message);
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.NoSourceChange,
                    snapshot.SourceFingerprint, Status.ProgramFingerprint,
                    Status.Diagnostics, Status.Message, stopwatch.Elapsed);
                return;
            }

            var compilation = await compiler.CompileAsync(snapshot, cancellationToken);
            if (generation != Volatile.Read(ref requestedGeneration))
            {
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.Superseded,
                    snapshot.SourceFingerprint, null, compilation.Diagnostics, null, stopwatch.Elapsed);
                return;
            }

            if (compilation.HasErrors || compilation.Result.Program is null)
            {
                lastRejectedSourceFingerprint = snapshot.SourceFingerprint;
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

            var version = await RunOnMainThreadAsync(
                () => materializer.Materialize(generation, compilation),
                cancellationToken);
            lastRejectedSourceFingerprint = null;
            if (generation != Volatile.Read(ref requestedGeneration))
            {
                PublishAttempt(projectId, generation, YarnHotReloadOutcome.Superseded,
                    version.SourceFingerprint, version.ProgramFingerprint,
                    version.Diagnostics, null, stopwatch.Elapsed);
                return;
            }

            var currentActive = versions.GetActiveVersion(projectId) ?? active;
            if (currentActive is not null && IsContentEquivalent(currentActive, version))
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
            if (playback?.IsPaused != true &&
                script.Phase == ScriptPlaybackPhase.AwaitingAdvance &&
                script.CanCaptureCheckpoint)
            {
                await ApplyCandidateAsync(version, cancellationToken);
                return;
            }

            if (script.Phase is not ScriptPlaybackPhase.Idle and not ScriptPlaybackPhase.Faulted)
            {
                lock (sync)
                {
                    pendingCandidate = version;
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
        if (playback?.IsPaused == true ||
            script.Phase != ScriptPlaybackPhase.AwaitingAdvance ||
            !script.CanCaptureCheckpoint)
        {
            return;
        }

        YarnProjectVersion? candidate;
        lock (sync)
        {
            candidate = pendingCandidate;
            pendingCandidate = null;
        }
        if (candidate is not null && lifetime?.IsCancellationRequested == false)
        {
            _ = ApplyCandidateSafelyAsync(candidate, lifetime.Token);
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

    private Task ApplyCandidateAsync(
        YarnProjectVersion candidate,
        CancellationToken cancellationToken) => RunOnMainThreadAsync(
            () => ApplyCandidateOnMainThreadAsync(candidate, cancellationToken),
            cancellationToken);

    private async Task ApplyCandidateOnMainThreadAsync(
        YarnProjectVersion candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.Generation != Volatile.Read(ref requestedGeneration))
        {
            return;
        }
        if (playback?.IsPaused == true ||
            script.Phase != ScriptPlaybackPhase.AwaitingAdvance ||
            !script.CanCaptureCheckpoint)
        {
            lock (sync)
            {
                if (candidate.Generation == Volatile.Read(ref requestedGeneration))
                {
                    pendingCandidate = candidate;
                }
            }
            return;
        }

        var spot = script.CurrentSpot;
        var running = versions.GetRunningVersion(candidate.ProjectId);
        if (spot is null || running is null ||
            !string.Equals(spot.Script.ProjectId, candidate.ProjectId, StringComparison.Ordinal) ||
            !candidate.NodeNames.Contains(spot.Script.NodeName) ||
            !candidate.LineNodes.TryGetValue(spot.LineId, out var candidateNode) ||
            !string.Equals(candidateNode, spot.Script.NodeName, StringComparison.Ordinal) ||
            candidate.ImplicitLineIds.Contains(spot.LineId))
        {
            var reason = $"Current line '{spot?.LineId ?? "<none>"}' is not a stable reload target; " +
                $"generation {candidate.Generation} will be used by the next dialogue session.";
            SetWatching(candidate.Generation, candidate.ProjectId,
                candidate.SourceFingerprint, candidate.ProgramFingerprint,
                candidate.Diagnostics, reason);
            logger.Warning(LogCategory, "script.hot_reload.live_apply_rejected", reason,
                properties: Properties(candidate.ProjectId, candidate.Generation,
                    candidate.SourceFingerprint, candidate.ProgramFingerprint,
                    YarnHotReloadOutcome.CompatibilityRejected));
            PublishAttempt(candidate.ProjectId, candidate.Generation,
                YarnHotReloadOutcome.CompatibilityRejected,
                candidate.SourceFingerprint, candidate.ProgramFingerprint,
                candidate.Diagnostics, reason, TimeSpan.Zero);
            return;
        }

        var checkpoint = script.CaptureCheckpoint();
        var variables = backend.VariableStore.Capture();
        var stopwatch = Stopwatch.StartNew();
        SetStatus(new YarnHotReloadStatus(
            YarnHotReloadState.Applying,
            candidate.Generation,
            candidate.ProjectId,
            candidate.SourceFingerprint,
            candidate.ProgramFingerprint,
            candidate.Diagnostics,
            null));
        logger.Info(LogCategory, "script.hot_reload.apply_started",
            $"Applying Yarn hot reload generation {candidate.Generation}.",
            properties: Properties(candidate.ProjectId, candidate.Generation,
                candidate.SourceFingerprint, candidate.ProgramFingerprint,
                YarnHotReloadOutcome.Applied));

        using var playbackPause = playback?.AcquirePause(PlaybackPauseReason.HotReload);
        backend.SetHotReloadProgressionFrozen(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(configuration.ApplyTimeoutMilliseconds);
            await backend.ReplaceRunningProjectAsync(
                candidate,
                checkpoint,
                DialoguePresentationOrigin.HotReloadReplay,
                timeout.Token);
            await WaitForStableBoundaryAsync(checkpoint.Spot, timeout.Token);
            versions.MarkRunning(candidate.ProjectId, candidate.Project);
            var message = $"Yarn hot reload generation {candidate.Generation} was applied.";
            SetWatching(candidate.Generation, candidate.ProjectId,
                candidate.SourceFingerprint, candidate.ProgramFingerprint,
                candidate.Diagnostics, message);
            logger.Info(LogCategory, "script.hot_reload.applied", message,
                properties: Properties(candidate.ProjectId, candidate.Generation,
                    candidate.SourceFingerprint, candidate.ProgramFingerprint,
                    YarnHotReloadOutcome.Applied));
            PublishAttempt(candidate.ProjectId, candidate.Generation,
                YarnHotReloadOutcome.Applied,
                candidate.SourceFingerprint, candidate.ProgramFingerprint,
                candidate.Diagnostics, message, stopwatch.Elapsed);
        }
        catch (Exception applyException)
        {
            SetStatus(Status with { State = YarnHotReloadState.RollingBack });
            logger.Warning(LogCategory, "script.hot_reload.rollback_started",
                "Yarn hot reload failed; restoring the last-known-good project.",
                applyException);
            try
            {
                versions.Activate(running);
                using var rollbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                rollbackTimeout.CancelAfter(configuration.ApplyTimeoutMilliseconds);
                await backend.ReplaceRunningProjectAsync(
                    running,
                    checkpoint,
                    DialoguePresentationOrigin.CheckpointRestore,
                    rollbackTimeout.Token);
                backend.VariableStore.ReplaceAll(variables);
                await WaitForStableBoundaryAsync(checkpoint.Spot, rollbackTimeout.Token);
                versions.MarkRunning(running.ProjectId, running.Project);
                var message = "Yarn hot reload was rolled back to the last-known-good project.";
                SetWatching(candidate.Generation, candidate.ProjectId,
                    candidate.SourceFingerprint, running.ProgramFingerprint,
                    candidate.Diagnostics, message);
                logger.Error(LogCategory, "script.hot_reload.rolled_back", message, applyException);
                PublishAttempt(candidate.ProjectId, candidate.Generation,
                    YarnHotReloadOutcome.RolledBack,
                    candidate.SourceFingerprint, running.ProgramFingerprint,
                    candidate.Diagnostics, message, stopwatch.Elapsed);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Yarn hot reload and rollback both failed.",
                    applyException,
                    rollbackException);
            }
        }
        finally
        {
            backend.SetHotReloadProgressionFrozen(false);
        }
    }

    private async Task WaitForBoundaryAsync(
        ScriptPlaybackSpot expected,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            if (script.Phase == ScriptPlaybackPhase.AwaitingAdvance)
            {
                var actual = script.CurrentSpot;
                if (actual == expected)
                {
                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"Yarn hot reload reached '{actual}' instead of '{expected}'."));
                }
            }
            else if (script.Phase is ScriptPlaybackPhase.Idle or ScriptPlaybackPhase.Faulted)
            {
                completion.TrySetException(new InvalidOperationException(
                    $"Yarn hot reload ended in phase '{script.Phase}'."));
            }
        }
        void Changed(ScriptPlaybackPhaseChanged _) => Observe();
        script.PhaseChanged += Changed;
        try
        {
            Observe();
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            script.PhaseChanged -= Changed;
        }
    }

    private async Task WaitForStableBoundaryAsync(
        ScriptPlaybackSpot expected,
        CancellationToken cancellationToken)
    {
        await WaitForBoundaryAsync(expected, cancellationToken);

        // DisplayCompleted is raised from inside Yarn's presenter callback. Let
        // that callback and the restored VM continuation fully settle before
        // auto/skip or player input can advance the replayed line.
        await YarnTask.NextFrame();
        await YarnTask.NextFrame();
        cancellationToken.ThrowIfCancellationRequested();

        if (script.Phase != ScriptPlaybackPhase.AwaitingAdvance ||
            script.CurrentSpot != expected)
        {
            throw new InvalidOperationException(
                "Yarn hot reload did not remain at its restored line boundary: " +
                $"expected '{expected}', actual '{script.CurrentSpot}' " +
                $"in phase '{script.Phase}'.");
        }
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

    private Task<T> RunOnMainThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (mainContext is null || ReferenceEquals(SynchronizationContext.Current, mainContext))
        {
            return Task.FromResult(action());
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        mainContext.Post(_ =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task;
    }

    private Task RunOnMainThreadAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (mainContext is null || ReferenceEquals(SynchronizationContext.Current, mainContext))
        {
            return action();
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mainContext.Post(async _ =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task;
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

    private static IReadOnlyDictionary<string, object?> Properties(
        string projectId,
        long generation,
        string? sourceFingerprint,
        string? programFingerprint,
        YarnHotReloadOutcome outcome) => new Dictionary<string, object?>
        {
            ["projectId"] = projectId,
            ["generation"] = generation,
            ["sourceFingerprint"] = sourceFingerprint,
            ["programFingerprint"] = programFingerprint,
            ["outcome"] = outcome.ToString(),
        };

    private static bool IsYarnPath(string path) =>
        path.EndsWith(".yarn", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(YarnSpinnerGodot.YarnProject.YARN_PROJECT_EXTENSION,
            StringComparison.OrdinalIgnoreCase);

    private static string RequireId(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        return value.Trim();
    }

    private static bool IsContentEquivalent(
        YarnProjectVersion active,
        YarnProjectVersion candidate)
    {
        if (!string.Equals(active.ProgramFingerprint, candidate.ProgramFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        if (!active.NodeNames.SetEquals(candidate.NodeNames) ||
            !active.LineIds.SetEquals(candidate.LineIds))
        {
            return false;
        }

        if (active.LineNodes.Count != candidate.LineNodes.Count)
        {
            return false;
        }

        foreach (var (lineId, node) in candidate.LineNodes)
        {
            if (!active.LineNodes.TryGetValue(lineId, out var activeNode) ||
                !string.Equals(activeNode, node, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!active.ImplicitLineIds.SetEquals(candidate.ImplicitLineIds))
        {
            return false;
        }

        var activeLoc = active.Project.baseLocalization;
        var candidateLoc = candidate.Project.baseLocalization;

        foreach (var lineId in candidate.LineIds)
        {
            var activeText = activeLoc?.GetLocalizedString(lineId);
            var candidateText = candidateLoc?.GetLocalizedString(lineId);
            if (!string.Equals(activeText, candidateText, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

