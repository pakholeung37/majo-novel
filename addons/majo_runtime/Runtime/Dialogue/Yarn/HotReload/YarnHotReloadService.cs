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
using YarnSpinnerGodot;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

public sealed class YarnHotReloadService : IYarnHotReloadService, IDisposable
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
    private readonly IStateManager? state;
    private readonly object sync = new();
    private readonly SemaphoreSlim compileGate = new(1, 1);
    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? pendingReload;
    private FileSystemWatcher? watcher;
    private System.Threading.Timer? consistencyTimer;
    private YarnHotReloadDispatcher? dispatcher;
    private long requestedGeneration;
    private bool enabled;
    private readonly Dictionary<string, YarnProjectVersion> pendingCandidates =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lastRejectedSourceFingerprints =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> observedSourceFingerprints =
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
        IStateManager? state = null)
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
        this.state = state;
    }

    public event Action<YarnHotReloadStatusChanged>? StatusChanged;

    public event Action<YarnHotReloadAttempt>? AttemptCompleted;

    public YarnHotReloadStatus Status { get; private set; } = YarnHotReloadStatus.Disabled;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dispatcher = new YarnHotReloadDispatcher(SynchronizationContext.Current);
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
        resetCancellation = new CancellationTokenSource();
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
            _ => _ = RunConsistencyScanAsync(defaultProjectId),
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
            observedSourceFingerprints.Clear();
        }
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

    private async Task RunConsistencyScanAsync(string projectId)
    {
        var currentLifetime = lifetime;
        if (!enabled || currentLifetime?.IsCancellationRequested != false)
        {
            return;
        }

        try
        {
            var snapshot = await YarnSourceSnapshotReader.ReadAsync(
                projectId,
                ResolveManifestPath(projectId),
                currentLifetime.Token);
            string? previous;
            lock (sync)
            {
                observedSourceFingerprints.TryGetValue(projectId, out previous);
                observedSourceFingerprints[projectId] = snapshot.SourceFingerprint;
            }

            // The first consistency scan establishes the baseline. In
            // particular, this avoids treating the imported project's empty
            // source fingerprint as a change during a cold start.
            if (previous is null)
            {
                versions.UpdateSourceFingerprint(projectId, snapshot.SourceFingerprint);
                return;
            }

            if (!string.Equals(previous, snapshot.SourceFingerprint, StringComparison.Ordinal))
            {
                QueueReload(projectId);
            }
        }
        catch (OperationCanceledException) when (currentLifetime.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.Warning(
                LogCategory,
                "script.hot_reload.consistency_scan_failed",
                $"Yarn consistency scan failed for project '{projectId}'.",
                exception);
        }
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
            lock (sync)
            {
                observedSourceFingerprints[projectId] = snapshot.SourceFingerprint;
            }
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
        if (playback?.IsPaused == true ||
            script.Phase != ScriptPlaybackPhase.AwaitingAdvance ||
            !script.CanCaptureCheckpoint)
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
        if (playback?.IsPaused == true ||
            script.Phase != ScriptPlaybackPhase.AwaitingAdvance ||
            !script.CanCaptureCheckpoint)
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

        var stableBoundary = state is IStateBoundarySource boundaries
            ? await boundaries.CaptureSaveBoundaryAsync(cancellationToken)
            : null;
        var checkpoint = stableBoundary?.Context.Script ?? script.CaptureCheckpoint();
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
            versions.Activate(candidate);
            if (state is not null && stableBoundary is not null)
            {
                var plan = await state.RestoreTransaction.PreflightAsync(
                    stableBoundary.Snapshot,
                    StateScope.Game,
                    StateOperationKind.HotReload,
                    timeout.Token);
                await state.RestoreTransaction.ExecuteAsync(plan, timeout.Token);
            }
            else
            {
                await backend.ReplaceRunningProjectAsync(
                    candidate,
                    checkpoint,
                    DialoguePresentationOrigin.HotReloadReplay,
                    timeout.Token);
            }
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
            if (state?.Status == StateManagerStatus.Faulted)
                throw;
            SetStatus(Status with { State = YarnHotReloadState.RollingBack });
            logger.Warning(LogCategory, "script.hot_reload.rollback_started",
                "Yarn hot reload failed; restoring the last-known-good project.",
                applyException);
            try
            {
                versions.Activate(running);
                using var rollbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                rollbackTimeout.CancelAfter(configuration.ApplyTimeoutMilliseconds);
                if (state is null || stableBoundary is null)
                {
                    await backend.ReplaceRunningProjectAsync(
                        running,
                        checkpoint,
                        DialoguePresentationOrigin.CheckpointRestore,
                        rollbackTimeout.Token);
                    backend.VariableStore.ReplaceAll(variables);
                    await WaitForStableBoundaryAsync(checkpoint.Spot, rollbackTimeout.Token);
                }
                versions.MarkRunning(running.ProjectId, running.Project);
                // Keep the successfully compiled candidate available for the
                // next session. A live apply can fail after the active version
                // has already been switched, so Activate(running) alone would
                // otherwise silently discard the candidate.
                versions.StageForNextSession(candidate);
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

    private static Dictionary<string, object?> Properties(
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

    public void Dispose()
    {
        compileGate.Dispose();
        applyGate.Dispose();
        lifetime?.Dispose();
        pendingReload?.Dispose();
        watcher?.Dispose();
        consistencyTimer?.Dispose();
        resetCancellation?.Dispose();
    }
}
