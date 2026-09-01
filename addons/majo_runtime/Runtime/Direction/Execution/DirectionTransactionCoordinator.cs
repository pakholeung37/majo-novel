#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly IPreparedDirectionTransaction prepared;
    private int status = (int)DirectionTransactionStatus.Committed;

    internal CommittedDirectionTransaction(
        DirectionPlan plan,
        DirectionCommitToken token,
        IPreparedDirectionTransaction prepared)
    {
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

    internal async Task FinishAsync(CancellationToken cancellationToken)
    {
        await prepared.FinishAsync(cancellationToken).ConfigureAwait(false);
        await Settlement.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveSettlementAsync()
    {
        try
        {
            await prepared.Settlement.ConfigureAwait(false);
            Interlocked.Exchange(ref status, (int)DirectionTransactionStatus.Settled);
        }
        catch
        {
            Interlocked.Exchange(ref status, (int)DirectionTransactionStatus.Faulted);
            throw;
        }
        finally
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public interface IDirectionTransactionCoordinator
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
    }

    public DirectionState State
    {
        get
        {
            lock (stateGate) return state;
        }
    }

    public async Task<CommittedDirectionTransaction> ExecuteAsync(
        IEnumerable<IDirectionDirective> directives,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directives);
        var capturedDirectives = directives.ToArray();
        CommittedDirectionTransaction committed;

        await commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = planner.Plan(State, capturedDirectives, completion);
            committed = await PrepareAndCommitAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commitGate.Release();
        }

        if (completion == DirectionCompletionBarrier.Settle)
            await committed.Settlement.WaitAsync(cancellationToken).ConfigureAwait(false);
        return committed;
    }

    public async Task<CommittedDirectionTransaction> RollbackAsync(
        CommittedDirectionTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
            await committed.Settlement.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        return transaction.FinishAsync(cancellationToken);
    }

    public async Task FinishSkippableAsync(CancellationToken cancellationToken = default)
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

        foreach (var transaction in active)
            await transaction.FinishAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommittedDirectionTransaction> PrepareAndCommitAsync(
        DirectionPlan plan,
        CancellationToken cancellationToken)
    {
        IPreparedDirectionTransaction? prepared = null;
        var didCommit = false;
        try
        {
            prepared = await backend.PrepareAsync(plan, cancellationToken).ConfigureAwait(false);
            if (prepared is null)
                throw new DirectionException("Direction backend returned no prepared transaction.");

            var token = new DirectionCommitToken(plan.Id, plan.TargetState.Revision);
            CommittedDirectionTransaction committed;
            lock (stateGate)
            {
                prepared.Commit(token);
                didCommit = true;
                committed = new CommittedDirectionTransaction(plan, token, prepared);
                state = plan.TargetState;
                transactions.Add(committed);
                transactions.RemoveAll(transaction =>
                    transaction.Status != DirectionTransactionStatus.Committed &&
                    !ReferenceEquals(transaction, committed));
            }

            return committed;
        }
        catch
        {
            if (!didCommit && prepared is not null)
                await prepared.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
