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

public sealed class CommittedDirectionTransaction
{
    private readonly object owner;
    private readonly IPreparedDirectionTransaction prepared;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private int status = (int)DirectionTransactionStatus.Committed;
    private int disposed;

    internal CommittedDirectionTransaction(
        object owner,
        DirectionPlan plan,
        DirectionCommitToken token,
        IPreparedDirectionTransaction prepared)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Plan = plan;
        Token = token;
        this.prepared = prepared;
        Settlement = ObserveSettlementAsync();
    }

    public DirectionPlan Plan { get; }
    public DirectionCommitToken Token { get; }
    public Task Settlement { get; }
    public DirectionTransactionStatus Status =>
        (DirectionTransactionStatus)Volatile.Read(ref status);

    public bool IsSkippable =>
        Plan.Transitions.Count > 0 && Plan.Transitions.All(transition => transition.Spec.Skippable);

    internal bool IsOwnedBy(object candidate) => ReferenceEquals(owner, candidate);

    internal async Task FinishAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
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
                await prepared.FinishAsync(cancellationToken);
        }
        finally
        {
            lifecycleGate.Release();
        }

        await Settlement.WaitAsync(cancellationToken);
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

    private async Task DisposeAsync()
    {
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                await prepared.DisposeAsync();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task ObserveSettlementAsync()
    {
        Exception? settlementException = null;
        try
        {
            await prepared.Settlement;
            Interlocked.Exchange(ref status, (int)DirectionTransactionStatus.Settled);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref status, (int)DirectionTransactionStatus.Faulted);
            settlementException = exception;
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

        if (settlementException is not null && disposeException is not null)
            throw new AggregateException(
                "Direction settlement and cleanup both failed.",
                settlementException,
                disposeException);
        if (settlementException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(settlementException)
                .Throw();
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
}

public sealed class DirectionTransactionCoordinator : IDirectionTransactionCoordinator
{
    private readonly IDirectionPlanner planner;
    private readonly IDirectionPreparationBackend backend;
    private readonly SemaphoreSlim commitGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly List<CommittedDirectionTransaction> transactions = new();
    private DirectionState state;

    public DirectionTransactionCoordinator(
        IDirectionPlanner planner,
        IDirectionPreparationBackend backend,
        DirectionState? initialState = null)
    {
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
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

    public async Task ResetAsync(CancellationToken cancellationToken)
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

        lock (stateGate)
        {
            transactions.Clear();
            state = DirectionState.CreateDefault();
        }

        if (failures is not null)
            throw new AggregateException("One or more Direction transactions failed to reset.", failures);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) =>
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

            var restored = transaction.Plan.BeforeState with
            {
                Revision = checked(current.Revision + 1),
            };
            var rollbackPlan = new DirectionPlan(
                DirectionTransactionId.New(),
                current,
                restored,
                new[]
                {
                    new DirectionTransition(
                        new[] { "*" },
                        DirectionTransitionSpec.Cut),
                },
                Array.Empty<ITransientDirectionCue>(),
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
            var current = State;
            var plan = new DirectionPlan(
                DirectionTransactionId.New(),
                current,
                target with { Revision = checked(current.Revision + 1) },
                new[] { new DirectionTransition(new[] { "*" }, DirectionTransitionSpec.Cut) },
                Array.Empty<ITransientDirectionCue>(),
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
        DirectionPlan plan,
        CancellationToken cancellationToken)
    {
        IPreparedDirectionTransaction? prepared = null;
        var commitStarted = false;
        var ownershipTransferred = false;
        DirectionCommitToken token = default;
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

            // Commit is deliberately outside stateGate. The commit gate still
            // serializes writers, while readers never block on Godot/backend work.
            commitStarted = true;
            prepared.Commit(token);

            lock (stateGate)
            {
                if (state.Revision != plan.BeforeState.Revision)
                    throw new DirectionRevisionException(
                        plan.BeforeState.Revision,
                        state.Revision);
                state = plan.TargetState;
            }

            var committed = new CommittedDirectionTransaction(this, plan, token, prepared);
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

            List<Exception>? cleanupFailures = null;
            if (commitStarted)
            {
                try { prepared.Rollback(token); }
                catch (Exception rollbackException)
                {
                    cleanupFailures = new List<Exception> { rollbackException };
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
}
