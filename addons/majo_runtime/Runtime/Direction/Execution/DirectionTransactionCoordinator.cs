#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Execution;

public enum DirectionTransactionStatus
{
    Committed,
    Settled,
    Faulted,
}

public sealed class CommittedDirectionTransaction : IAsyncDisposable
{
    private readonly object owner;
    private readonly IPreparedDirectionTransaction prepared;
    private readonly ITransientCueActivity? cueActivity;
    private readonly IDisposable? activityLease;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private int status = (int)DirectionTransactionStatus.Committed;
    private int disposed;

    internal CommittedDirectionTransaction(
        object owner,
        DirectionExecutionPlan executionPlan,
        DirectionCommitToken token,
        IPreparedDirectionTransaction prepared,
        IDisposable? activityLease = null,
        ITransientCueActivity? cueActivity = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ExecutionPlan = executionPlan;
        Token = token;
        this.prepared = prepared;
        this.activityLease = activityLease;
        this.cueActivity = cueActivity;
        Settlement = ObserveSettlementAsync();
    }

    internal CommittedDirectionTransaction(
        object owner,
        DirectionPlan plan,
        DirectionCommitToken token,
        IPreparedDirectionTransaction prepared,
        IDisposable? activityLease = null,
        ITransientCueActivity? cueActivity = null)
        : this(
            owner,
            new DirectionExecutionPlan(plan, Array.Empty<DirectionResourceClaim>(), DirectionCompletionBarrier.Commit),
            token,
            prepared,
            activityLease,
            cueActivity)
    {
    }

    public DirectionExecutionPlan ExecutionPlan { get; }
    public DirectionPlan Plan => ExecutionPlan.DomainPlan;
    public DirectionCommitToken Token { get; }
    public Task Settlement { get; }
    public DirectionTransactionStatus Status =>
        (DirectionTransactionStatus)Volatile.Read(ref status);

    internal ITransientCueActivity? CueActivity => cueActivity;

    public bool IsSkippable
    {
        get
        {
            var hasTransitions = Plan.Transitions.Count > 0;
            var hasCues = cueActivity is not null || Plan.Cues.Count > 0;
            if (!hasTransitions && !hasCues)
                return false;

            var transitionsSkippable = !hasTransitions ||
                Plan.Transitions.All(transition => transition.Spec.Skippable);
            var cuesSkippable = cueActivity is null || cueActivity.IsSkippable;
            return transitionsSkippable && cuesSkippable;
        }
    }

    internal bool IsOwnedBy(object candidate) => ReferenceEquals(owner, candidate);

    internal async Task FinishAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        List<Exception>? failures = null;
        try
        {
            if (Volatile.Read(ref disposed) != 0 &&
                Status == DirectionTransactionStatus.Settled)
                return;

            if (Status == DirectionTransactionStatus.Faulted)
            {
                await Settlement.WaitAsync(cancellationToken);
                return;
            }

            if (!prepared.Settlement.IsCompleted)
            {
                try
                {
                    await prepared.FinishAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }

            if (cueActivity is not null && !cueActivity.Settlement.IsCompleted)
            {
                try
                {
                    await cueActivity.FinishAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        try
        {
            await Settlement.WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("Direction transaction finish failed.", failures);
        }
    }

    internal async Task FinishSceneAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            List<Exception>? errors = null;
            try { await prepared.FinishSceneAsync(cancellationToken); }
            catch (Exception error) { (errors ??= new()).Add(error); }
            if (cueActivity is not null && !cueActivity.Settlement.IsCompleted)
                try { await cueActivity.FinishAsync(cancellationToken); }
                catch (Exception error) { (errors ??= new()).Add(error); }
            if (errors is not null) throw new AggregateException("Scene transaction failed to finish.", errors);
        }
        finally { lifecycleGate.Release(); }
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        try
        {
            if (Status == DirectionTransactionStatus.Committed)
                await FinishAsync(cancellationToken);
            else
                await Settlement.WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            failures = new List<Exception> { exception };
        }

        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>()).Add(exception);
        }

        if (failures is not null)
            throw new AggregateException("Direction transaction cleanup failed.", failures);
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        List<Exception>? failures = null;
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                activityLease?.Dispose();

                if (cueActivity is not null)
                {
                    try
                    {
                        await cueActivity.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        (failures ??= new List<Exception>()).Add(exception);
                    }
                }

                try
                {
                    await prepared.DisposeAsync();
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("Direction transaction disposal failed.", failures);
        }
    }

    private async Task ObserveSettlementAsync()
    {
        List<Exception>? settlementExceptions = null;
        var task = cueActivity is not null
            ? Task.WhenAll(prepared.Settlement, cueActivity.Settlement)
            : prepared.Settlement;
        try
        {
            await task;
            Interlocked.Exchange(ref status, (int)DirectionTransactionStatus.Settled);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref status, (int)DirectionTransactionStatus.Faulted);
            settlementExceptions = task.Exception is not null
                ? task.Exception.Flatten().InnerExceptions.ToList()
                : new List<Exception> { exception };
        }

        Exception? disposeException = null;
        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            disposeException = exception;
        }

        if (settlementExceptions is not null && disposeException is not null)
        {
            settlementExceptions.Add(disposeException);
            throw new AggregateException(
                "Direction settlement and cleanup both failed.",
                settlementExceptions);
        }
        if (settlementExceptions is not null)
        {
            if (settlementExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(settlementExceptions[0])
                    .Throw();
            }
            throw new AggregateException("Direction settlement failed.", settlementExceptions);
        }
        if (disposeException is not null)
            throw disposeException;
    }
}

public interface IDirectionTransactionCoordinator : IRuntimeService
{
    DirectionState State { get; }

    Task<CommittedDirectionTransaction> ExecuteAsync(
        IEnumerable<IDirectionDirective> directives,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit,
        CancellationToken cancellationToken = default);

    Task<CommittedDirectionTransaction> RollbackAsync(
        CommittedDirectionTransaction transaction,
        CancellationToken cancellationToken = default);

    Task FinishAsync(
        CommittedDirectionTransaction transaction,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(
        DirectionState target,
        CancellationToken cancellationToken = default);

    Task FinishSkippableAsync(CancellationToken cancellationToken = default);
    Task ClearSceneAsync(DirectionSceneContext context, Func<CancellationToken, Task> stopAudio,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Scene boundaries are not supported by this backend.");
}

public sealed class DirectionTransactionCoordinator : IDirectionTransactionCoordinator, IDisposable
{
    private static readonly string[] WildcardSelector = ["*"];
    private static readonly DirectionTransition[] FallbackCutTransitions =
    [
        new(WildcardSelector, DirectionTransitionSpec.Cut),
    ];
    private readonly IDirectionPlanner planner;
    private readonly IDirectionPreparationBackend backend;
    private readonly IRuntimeActivityRegistry? activities;
    private readonly ITransientDirectionCueExecutor? cues;
    private readonly Func<CancellationToken, Task>? resetRendering;
    private readonly Func<CancellationToken, Task>? clearSceneRendering;
    private bool sceneClosing;
    private readonly SemaphoreSlim commitGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly List<CommittedDirectionTransaction> transactions = new();
    private DirectionState state;

    public DirectionTransactionCoordinator(
        IDirectionPlanner planner,
        IDirectionPreparationBackend backend,
        DirectionState? initialState = null,
        IRuntimeActivityRegistry? activities = null,
        ITransientDirectionCueExecutor? cues = null,
        Func<CancellationToken, Task>? resetRendering = null,
        Func<CancellationToken, Task>? clearSceneRendering = null)
    {
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.activities = activities;
        this.cues = cues;
        this.resetRendering = resetRendering;
        this.clearSceneRendering = clearSceneRendering;
        state = initialState ?? DirectionState.CreateDefault();
        state.Validate();
    }

    public DirectionState State
    {
        get
        {
            lock (stateGate) return state;
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await commitGate.WaitAsync(cancellationToken);
        try
        {
            await ResetUnderCommitGateAsync(cancellationToken);
        }
        finally
        {
            commitGate.Release();
        }
    }

    private async Task ResetUnderCommitGateAsync(CancellationToken cancellationToken)
    {
        CommittedDirectionTransaction[] active;
        lock (stateGate)
            active = transactions.ToArray();

        List<Exception>? failures = null;
        foreach (var transaction in active)
        {
            try
            {
                await transaction.CleanupAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }
        }

        // Committed targets outlive their transactions; release them before restoring state.
        if (resetRendering is not null)
        {
            try { await resetRendering(cancellationToken); }
            catch (Exception exception)
            {
                (failures ??= new List<Exception>()).Add(exception);
            }
        }

        lock (stateGate)
        {
            transactions.Clear();
            state = DirectionState.CreateDefault();
            sceneClosing = false;
        }

        if (failures is not null)
            throw new AggregateException("One or more Direction transactions failed to reset.", failures);
    }

    public async Task ClearSceneAsync(DirectionSceneContext context, Func<CancellationToken, Task> stopAudio,
        CancellationToken cancellationToken = default)
    {
        await commitGate.WaitAsync(cancellationToken);
        try
        {
            sceneClosing = true;
            CommittedDirectionTransaction[] owned;
            lock (stateGate) owned = transactions.ToArray();
            List<Exception>? errors = null;
            foreach (var transaction in owned)
                try { await transaction.FinishSceneAsync(CancellationToken.None); }
                catch (Exception error) { (errors ??= new()).Add(error); }
            try { await stopAudio(CancellationToken.None); }
            catch (Exception error) { (errors ??= new()).Add(error); }
            if (clearSceneRendering is not null)
                try { await clearSceneRendering(CancellationToken.None); }
                catch (Exception error) { (errors ??= new()).Add(error); }
            if (errors is not null) throw new AggregateException("Scene boundary cleanup failed.", errors);
            lock (stateGate)
            {
                state = DirectionState.CreateDefault() with
                { Scene = context, Effects = state.Effects, Revision = checked(state.Revision + 1) };
                transactions.RemoveAll(transaction => transaction.Status != DirectionTransactionStatus.Committed);
            }
            sceneClosing = false;
        }
        finally { commitGate.Release(); }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        ResetAsync(cancellationToken);

    public async Task<CommittedDirectionTransaction> ExecuteAsync(
        IEnumerable<IDirectionDirective> directives,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directives);
        var capturedDirectives = directives.ToArray();
        CommittedDirectionTransaction committed;

        await commitGate.WaitAsync(cancellationToken);
        try
        {
            if (sceneClosing) throw new DirectionException("Scene context is closing or failed; reset before submitting new commands.");
            var plan = planner.Plan(State, capturedDirectives, completion);
            committed = await PrepareAndCommitAsync(plan, cancellationToken);
        }
        finally
        {
            commitGate.Release();
        }

        if (completion == DirectionCompletionBarrier.Settle)
            await committed.Settlement.WaitAsync(cancellationToken);
        return committed;
    }

    public async Task<CommittedDirectionTransaction> RollbackAsync(
        CommittedDirectionTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!transaction.IsOwnedBy(this))
            throw new ArgumentException(
                "The Direction transaction belongs to another coordinator.",
                nameof(transaction));
        await commitGate.WaitAsync(cancellationToken);
        try
        {
            var current = State;
            if (current.Revision != transaction.Plan.TargetState.Revision)
            {
                throw new DirectionRevisionException(
                    transaction.Plan.TargetState.Revision,
                    current.Revision);
            }

            if (transaction.CueActivity is not null &&
                !transaction.CueActivity.Settlement.IsCompleted)
            {
                await transaction.CueActivity.CancelAsync(cancellationToken);
            }

            var restored = transaction.Plan.BeforeState with
            {
                Revision = checked(current.Revision + 1),
            };
            var rollbackPlan = new DirectionExecutionPlan(
                new DirectionPlan(
                    DirectionTransactionId.New(),
                    current,
                    restored,
                    FallbackCutTransitions,
                    Array.Empty<ITransientDirectionCue>()),
                Array.Empty<DirectionResourceClaim>(),
                DirectionCompletionBarrier.Settle);
            var committed = await PrepareAndCommitAsync(
                rollbackPlan,
                cancellationToken);
            await committed.Settlement.WaitAsync(cancellationToken);
            return committed;
        }
        finally
        {
            commitGate.Release();
        }
    }

    public Task FinishAsync(
        CommittedDirectionTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!transaction.IsOwnedBy(this))
            throw new ArgumentException(
                "The Direction transaction belongs to another coordinator.",
                nameof(transaction));
        return FinishUnderCommitGateAsync(transaction, cancellationToken);
    }

    private async Task FinishUnderCommitGateAsync(
        CommittedDirectionTransaction transaction,
        CancellationToken cancellationToken)
    {
        await commitGate.WaitAsync(cancellationToken);
        try { await transaction.FinishAsync(cancellationToken); }
        finally { commitGate.Release(); }
    }

    public async Task RestoreAsync(
        DirectionState target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        await commitGate.WaitAsync(cancellationToken);
        try
        {
            CommittedDirectionTransaction[] active;
            lock (stateGate) active = transactions.ToArray();
            foreach (var transaction in active)
                if (transaction.Status == DirectionTransactionStatus.Committed)
                    await transaction.FinishAsync(cancellationToken);
            var current = State;
            var plan = new DirectionExecutionPlan(
                new DirectionPlan(
                    DirectionTransactionId.New(),
                    current,
                    target with
                    {
                        Revision = checked(current.Revision + 1),
                        Scene = target.Scene with
                        { Epoch = checked(Math.Max(current.Scene.Epoch, target.Scene.Epoch) + 1) }
                    },
                    FallbackCutTransitions,
                    Array.Empty<ITransientDirectionCue>()),
                Array.Empty<DirectionResourceClaim>(),
                DirectionCompletionBarrier.Settle);
            var committed = await PrepareAndCommitAsync(plan, cancellationToken);
            await committed.Settlement.WaitAsync(cancellationToken);
        }
        finally
        {
            commitGate.Release();
        }
    }

    public async Task FinishSkippableAsync(CancellationToken cancellationToken = default)
    {
        await commitGate.WaitAsync(cancellationToken);
        try
        {
            CommittedDirectionTransaction[] active;
            lock (stateGate)
            {
                active = transactions
                    .Where(transaction =>
                        transaction.Status == DirectionTransactionStatus.Committed &&
                        transaction.IsSkippable)
                    .ToArray();
            }

            List<Exception>? failures = null;
            foreach (var transaction in active)
            {
                try
                {
                    await transaction.FinishAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    (failures ??= new List<Exception>()).Add(exception);
                }
            }
            if (failures is not null)
                throw new AggregateException("One or more Direction transitions failed to skip.", failures);
        }
        finally
        {
            commitGate.Release();
        }
    }

    private async Task<CommittedDirectionTransaction> PrepareAndCommitAsync(
        DirectionExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Cues.Count > 0)
        {
            if (cues is null)
                throw new DirectionException(
                    $"Direction plan contains {plan.Cues.Count} transient cues, but no cue executor is configured.");
            cues.Validate(plan.Cues);
        }

        IPreparedDirectionTransaction? prepared = null;
        ITransientCueActivity? cueActivity = null;
        var commitStarted = false;
        var ownershipTransferred = false;
        DirectionCommitToken token = default;
        IDisposable? activityLease = null;
        try
        {
            prepared = await backend.PrepareAsync(plan, cancellationToken);
            if (prepared is null)
                throw new DirectionException("Direction backend returned no prepared transaction.");

            token = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);
            lock (stateGate)
            {
                if (!ReferenceEquals(state, plan.BeforeState) &&
                    state.Revision != plan.BeforeState.Revision)
                {
                    throw new DirectionRevisionException(
                        plan.BeforeState.Revision,
                        state.Revision);
                }
            }

            activityLease = activities?.BeginActivity(
                RuntimeActivityOwner.Direction,
                "Transaction",
                plan.Id.Value.ToString(),
                isCancelable: true,
                isFinishable: true);

            // Commit is deliberately outside stateGate. The commit gate still
            // serializes writers, while readers never block on Godot/backend work.
            commitStarted = true;
            prepared.Commit(token);
            var committedState = plan.DomainPlan.ResolveCommitState();
            plan = plan with { DomainPlan = plan.DomainPlan with { TargetState = committedState } };

            lock (stateGate)
            {
                if (state.Revision != plan.BeforeState.Revision)
                    throw new DirectionRevisionException(
                        plan.BeforeState.Revision,
                        state.Revision);
                state = plan.TargetState;
            }

            if (plan.Cues.Count > 0 && cues is not null)
            {
                cueActivity = await cues.StartCommittedAsync(
                    plan.Cues,
                    token,
                    plan.Completion,
                    cancellationToken);
            }

            prepared.ConfirmCommit();
            var committed = new CommittedDirectionTransaction(
                this, plan, token, prepared, activityLease, cueActivity);
            ownershipTransferred = true;
            lock (stateGate)
            {
                transactions.Add(committed);
                transactions.RemoveAll(transaction =>
                    transaction.Status != DirectionTransactionStatus.Committed &&
                    !ReferenceEquals(transaction, committed));
            }
            return committed;
        }
        catch (Exception exception)
        {
            if (prepared is null || ownershipTransferred)
                throw;

            activityLease?.Dispose();

            List<Exception>? cleanupFailures = null;
            if (cueActivity is not null)
            {
                try
                {
                    await cueActivity.CancelAsync(cancellationToken);
                    await cueActivity.DisposeAsync();
                }
                catch (Exception cueCancelException)
                {
                    (cleanupFailures ??= new List<Exception>()).Add(cueCancelException);
                }
            }

            if (commitStarted)
            {
                try { prepared.Rollback(token); }
                catch (Exception rollbackException)
                {
                    (cleanupFailures ??= new List<Exception>()).Add(rollbackException);
                }
            }
            try
            {
                await prepared.DisposeAsync();
            }
            catch (Exception disposeException)
            {
                (cleanupFailures ??= new List<Exception>()).Add(disposeException);
            }

            if (commitStarted)
            {
                lock (stateGate)
                {
                    if (state.Revision == plan.TargetState.Revision &&
                        Equals(state, plan.TargetState))
                        state = plan.BeforeState;
                }
            }
            if (cleanupFailures is not null)
                throw new AggregateException(
                    "Direction transaction failed and its prepared resources could not be fully released.",
                    new[] { exception }.Concat(cleanupFailures));
            throw;
        }
    }

    public void Dispose()
    {
        commitGate.Dispose();
    }
}
