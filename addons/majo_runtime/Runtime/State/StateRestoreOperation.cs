#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Input;
using Majo.Runtime.Script;

namespace Majo.Runtime.State;

internal enum StateRestoreStage { Stop, Prepare, Commit, Presentation, Activation, Release }
internal sealed record StateRestoreFailure(Exception Error, bool Faulted, bool RecoveredBoundary);

/// <summary>Owns a single prepared restore, its candidates and its resource leases.</summary>
internal sealed class StateRestoreOperation(
    IReadOnlyList<IStateContributor> contributors,
    IRuntimeRestoreBoundary boundary,
    IRuntimeLogger logger,
    TimeSpan presentationTimeout) : IDisposable
{
    private readonly List<IPreparedStateRestore> candidates = [];
    private StateRestoreResources? resources = new();
    public StateRestoreStage Stage { get; private set; }
    public string StageName => Stage.ToString().ToLowerInvariant();

    public async Task AcquirePlaybackAsync(ILineBoundarySource? source, CancellationToken token)
    {
        if (source is not null) resources!.PlaybackLease = await source.AcquireRestoreAsync(token);
    }

    public void AcquireInput(IInputManager input, object owner) =>
        resources!.InputMuter = input.AcquireMuter(owner, new InputMuteOptions(name: "state-restore"));

    public async Task ExecuteAsync(StateRestorePlan plan, CancellationToken token)
    {
        resources!.Output = (boundary as IRuntimeRestoreOutput)?.RetainRestoreOutput();
        if (plan.Operation == StateOperationKind.HotReload && boundary is IRuntimeHotReloadBoundary hotReload)
            await hotReload.CancelForHotReloadAsync(token);
        else await boundary.CancelForRestoreAsync(token);

        Stage = StateRestoreStage.Prepare;
        var context = new RestoreContext(plan.Scope, plan.Records, plan.Operation);
        foreach (var contributor in contributors)
        {
            token.ThrowIfCancellationRequested();
            var preparer = contributor as IPreparedStateContributor ?? throw new StateCompatibilityException(
                $"Contributor '{contributor.Key}' does not implement prepared replacement.");
            plan.Records.TryGetValue(contributor.Key, out var record);
            candidates.Add(await preparer.PrepareRestoreAsync(record, context, token) ??
                throw new StateRuntimeException($"Contributor '{contributor.Key}' returned no restore candidate."));
        }
        logger.Info("state", "state.restore_prepared", "All restore candidates are prepared.");
        token.ThrowIfCancellationRequested();
        Stage = StateRestoreStage.Commit;
        // Commit is synchronous and deliberately contains no cancellation points.
        foreach (var candidate in candidates) candidate.Commit();
        logger.Info("state", "state.restore_committed", "Committed all prepared state replacements.");
        Stage = StateRestoreStage.Presentation;
        using (var deadline = new CancellationTokenSource(presentationTimeout))
        {
            foreach (var candidate in candidates.OfType<IStateRestorePresentation>())
                await candidate.WaitForPresentationAsync(deadline.Token).WaitAsync(deadline.Token);
            if (resources.Output is not null)
                await resources.Output.WaitForTargetAsync(deadline.Token).WaitAsync(deadline.Token);
        }
        logger.Info("state", "state.restore_presented", "Target presentation is ready for script activation.");
        Stage = StateRestoreStage.Activation;
        using (var deadline = new CancellationTokenSource(presentationTimeout))
            foreach (var activation in candidates.OfType<IStateRestoreActivation>())
                await activation.ActivateAsync(deadline.Token).WaitAsync(deadline.Token);
        Stage = StateRestoreStage.Release;
        await DisposeCandidatesAsync();
        resources.Output?.Dispose();
        resources.Output = null;
    }

    public async Task<StateRestoreFailure> RecoverAsync(Exception failure, StateOperationKind operation,
        CommittedLineBoundary? oldBoundary, ILineBoundarySource? source)
    {
        Exception reported = failure;
        var faulted = Stage >= StateRestoreStage.Commit;
        var recovered = false;
        try { await DisposeCandidatesAsync(); }
        catch (Exception cleanup)
        {
            reported = new AggregateException("Restore and candidate cleanup failed.", reported, cleanup);
            faulted = true;
        }
        if (!faulted && oldBoundary is not null && source is not null)
        {
            try
            {
                var origin = operation == StateOperationKind.HotReload
                    ? DialoguePresentationOrigin.HotReloadReplay : DialoguePresentationOrigin.CheckpointRestore;
                await source.ResumeBoundaryAsync(oldBoundary.Context.Script, origin, CancellationToken.None);
                recovered = true;
            }
            catch (Exception recovery)
            {
                reported = new AggregateException("Restore preparation and old boundary recovery failed.", reported, recovery);
                faulted = true;
            }
        }
        return new(reported, faulted, recovered);
    }

    public StateRestoreResources RetainFaultResources() =>
        Interlocked.Exchange(ref resources, null) ?? throw new InvalidOperationException("Restore leases already transferred.");

    private async ValueTask DisposeCandidatesAsync()
    {
        List<Exception>? failures = null;
        while (candidates.Count > 0)
        {
            var candidate = candidates[^1];
            candidates.RemoveAt(candidates.Count - 1);
            try { await candidate.DisposeAsync(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null) throw new AggregateException("Restore candidate cleanup failed.", failures);
    }

    public void Dispose() => Interlocked.Exchange(ref resources, null)?.Dispose();
}
