#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Auto;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using YarnSpinnerGodot;
using static Majo.Runtime.Dialogue.Yarn.HotReload.YarnHotReloadLog;

namespace Majo.Runtime.Dialogue.Yarn.HotReload;

// Runs on the engine thread. Filesystem events, compilation generations and dispatch belong to the service.
internal sealed class YarnHotReloadApplier
{
    private const string LogCategory = "script.hot_reload";
    private readonly IYarnProjectVersionStore versions;
    private readonly IScriptPlayback script;
    private readonly IYarnDialogueBackend backend;
    private readonly YarnHotReloadConfiguration configuration;
    private readonly IPlaybackModeManager? playback;
    private readonly IStateManager? state;
    private readonly IRuntimeLogger logger;

    public YarnHotReloadApplier(IYarnProjectVersionStore versions, IScriptPlayback script,
        IYarnDialogueBackend backend, YarnHotReloadConfiguration configuration,
        IPlaybackModeManager? playback, IStateManager? state, IRuntimeLogger logger)
    {
        this.versions = versions;
        this.script = script;
        this.backend = backend;
        this.configuration = configuration;
        this.playback = playback;
        this.state = state;
        this.logger = logger;
    }

    public bool IsAtSafePoint => playback?.IsPaused != true &&
        script.Phase == ScriptPlaybackPhase.AwaitingAdvance && script.CanCaptureCheckpoint;

    public async Task<YarnHotReloadAttempt> ApplyAsync(YarnProjectVersion candidate,
        Action<YarnHotReloadState> transition, CancellationToken cancellationToken)
    {
        var spot = script.CurrentSpot;
        var running = versions.GetRunningVersion(candidate.ProjectId);
        if (spot is null || running is null ||
            !string.Equals(spot.Script.ProjectId, candidate.ProjectId, StringComparison.Ordinal) ||
            !candidate.NodeNames.Contains(spot.Script.NodeName) ||
            !candidate.LineNodes.TryGetValue(spot.LineId, out var node) ||
            !string.Equals(node, spot.Script.NodeName, StringComparison.Ordinal) ||
            candidate.ImplicitLineIds.Contains(spot.LineId))
        {
            var reason = $"Current line '{spot?.LineId ?? "<none>"}' is not a stable reload target; " +
                $"generation {candidate.Generation} will be used by the next dialogue session.";
            logger.Warning(LogCategory, "script.hot_reload.live_apply_rejected", reason,
                properties: Properties(candidate.ProjectId, candidate.Generation, candidate.SourceFingerprint,
                    candidate.ProgramFingerprint, YarnHotReloadOutcome.CompatibilityRejected));
            return Result(candidate, YarnHotReloadOutcome.CompatibilityRejected, reason, TimeSpan.Zero);
        }

        var boundary = state is IStateBoundarySource boundaries
            ? await boundaries.CaptureSaveBoundaryAsync(cancellationToken) : null;
        var checkpoint = boundary?.Context.Script ?? script.CaptureCheckpoint();
        if ((checkpoint.CallStack.Count > 0 || checkpoint.OperandStack.Count > 0) &&
            !string.Equals(candidate.ProgramFingerprint, checkpoint.ProgramFingerprint, StringComparison.Ordinal))
            return Result(candidate, YarnHotReloadOutcome.CompatibilityRejected,
                "A nested script checkpoint requires the same compiled program; the candidate will be used by the next dialogue session.",
                TimeSpan.Zero);

        var variables = backend.VariableStore.Capture();
        var stopwatch = Stopwatch.StartNew();
        transition(YarnHotReloadState.Applying);
        logger.Info(LogCategory, "script.hot_reload.apply_started",
            $"Applying Yarn hot reload generation {candidate.Generation}.",
            properties: Properties(candidate.ProjectId, candidate.Generation, candidate.SourceFingerprint,
                candidate.ProgramFingerprint, YarnHotReloadOutcome.Applied));
        using var pause = playback?.AcquirePause(PlaybackPauseReason.HotReload);
        backend.SetHotReloadProgressionFrozen(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(configuration.ApplyTimeoutMilliseconds);
            versions.Activate(candidate);
            if (state is not null && boundary is not null)
            {
                var plan = await state.RestoreTransaction.PreflightAsync(boundary.Snapshot,
                    StateScope.Game, StateOperationKind.HotReload, timeout.Token);
                await state.RestoreTransaction.ExecuteAsync(plan, timeout.Token);
            }
            else await backend.ReplaceRunningProjectAsync(candidate, checkpoint,
                DialoguePresentationOrigin.HotReloadReplay, timeout.Token);
            await WaitForStableBoundaryAsync(checkpoint.Spot, timeout.Token);
            versions.MarkRunning(candidate.ProjectId, candidate.Project);
            var message = $"Yarn hot reload generation {candidate.Generation} was applied.";
            logger.Info(LogCategory, "script.hot_reload.applied", message,
                properties: Properties(candidate.ProjectId, candidate.Generation, candidate.SourceFingerprint,
                    candidate.ProgramFingerprint, YarnHotReloadOutcome.Applied));
            return Result(candidate, YarnHotReloadOutcome.Applied, message, stopwatch.Elapsed);
        }
        catch (Exception applyException)
        {
            if (state?.Status == StateManagerStatus.Faulted) throw;
            transition(YarnHotReloadState.RollingBack);
            logger.Warning(LogCategory, "script.hot_reload.rollback_started",
                "Yarn hot reload failed; restoring the last-known-good project.", applyException);
            try
            {
                versions.Activate(running);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(configuration.ApplyTimeoutMilliseconds);
                if (state is null || boundary is null)
                {
                    await backend.ReplaceRunningProjectAsync(running, checkpoint,
                        DialoguePresentationOrigin.CheckpointRestore, timeout.Token);
                    backend.VariableStore.ReplaceAll(variables);
                    await WaitForStableBoundaryAsync(checkpoint.Spot, timeout.Token);
                }
                versions.MarkRunning(running.ProjectId, running.Project);
                versions.StageForNextSession(candidate);
                var message = "Yarn hot reload was rolled back to the last-known-good project.";
                logger.Error(LogCategory, "script.hot_reload.rolled_back", message, applyException);
                return Result(candidate, YarnHotReloadOutcome.RolledBack, message, stopwatch.Elapsed,
                    running.ProgramFingerprint);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("Yarn hot reload and rollback both failed.", applyException, rollbackException);
            }
        }
        finally { backend.SetHotReloadProgressionFrozen(false); }
    }

    private static YarnHotReloadAttempt Result(YarnProjectVersion candidate, YarnHotReloadOutcome outcome,
        string message, TimeSpan elapsed, string? programFingerprint = null) => new(
            candidate.ProjectId, candidate.Generation, outcome, candidate.SourceFingerprint,
            programFingerprint ?? candidate.ProgramFingerprint, candidate.Diagnostics, message, elapsed);

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

 }

internal static class YarnHotReloadLog
{
    internal static Dictionary<string, object?> Properties(
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

}
