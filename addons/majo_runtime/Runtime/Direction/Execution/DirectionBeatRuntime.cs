#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Execution;

public sealed record DirectionBeatDefaults(
    double? DurationSeconds = null,
    DirectionEase? Ease = null);

public interface IDirectionBeatRuntime : IRuntimeService
{
    bool HasOpenBeat { get; }
    string? OpenBeatId { get; }
    DirectionBeatDefaults? OpenBeatDefaults { get; }

    void Begin(
        string beatId,
        string nodeId,
        DirectionBeatDefaults? defaults = null);

    Task<CommittedDirectionTransaction?> SubmitAsync(
        IDirectionDirective directive,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit,
        CancellationToken cancellationToken = default);

    Task<CommittedDirectionTransaction> EndAsync(
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Settle,
        CancellationToken cancellationToken = default);

    void AbortOpen();

    Task JoinAsync(string beatId, CancellationToken cancellationToken = default);

    Task FinishAsync(string beatId, CancellationToken cancellationToken = default);

    void ValidateNodeBoundary(string nodeId);
}

public sealed class DirectionBeatRuntime : IDirectionBeatRuntime, IDisposable
{
    private readonly IDirectionTransactionCoordinator transactions;
    private readonly ITransientDirectionCueExecutor? cues;
    private readonly object sync = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly Dictionary<string, CommittedDirectionTransaction> committed =
        new(StringComparer.Ordinal);
    private OpenBeat? open;
    private bool operationInFlight;

    public DirectionBeatRuntime(
        IDirectionTransactionCoordinator transactions,
        ITransientDirectionCueExecutor? cues = null)
    {
        this.transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        this.cues = cues;
    }

    public bool HasOpenBeat { get { lock (sync) return open is not null; } }
    public string? OpenBeatId { get { lock (sync) return open?.Id; } }
    public DirectionBeatDefaults? OpenBeatDefaults { get { lock (sync) return open?.Defaults; } }

    public void Begin(
        string beatId,
        string nodeId,
        DirectionBeatDefaults? defaults = null)
    {
        beatId = RequireName(beatId, nameof(beatId));
        nodeId = RequireName(nodeId, nameof(nodeId));
        if (defaults?.DurationSeconds is { } duration &&
            (!double.IsFinite(duration) || duration < 0))
        {
            throw new DirectionBeatException(
                "Direction Beat default duration must be finite and non-negative.");
        }

        lock (sync)
        {
            if (operationInFlight)
                throw new DirectionBeatException(
                    "A Direction Beat operation is still in progress.");
            if (open is not null)
                throw new DirectionBeatException(
                    $"Beat '{open.Id}' is already open; Direction Beats cannot nest.");
            if (committed.TryGetValue(beatId, out var existing) &&
                existing.Status == DirectionTransactionStatus.Committed)
                throw new DirectionBeatException(
                    $"Beat '{beatId}' already names an unsettled transaction.");
            open = new OpenBeat(beatId, nodeId, defaults, new List<IDirectionDirective>());
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                operationInFlight = true;
                open = null;
                committed.Clear();
            }
        }
        finally
        {
            lock (sync) operationInFlight = false;
            operationGate.Release();
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) =>
        ResetAsync(cancellationToken);

    public async Task<CommittedDirectionTransaction?> SubmitAsync(
        IDirectionDirective directive,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (open is not null)
            {
                open.Directives.Add(directive);
                return null;
            }
        }

        await operationGate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                if (open is not null)
                {
                    open.Directives.Add(directive);
                    return null;
                }
                operationInFlight = true;
            }

            var committed = await transactions.ExecuteAsync(
                PrepareDirectives(new[] { directive }),
                completion,
                cancellationToken);
            return committed;
        }
        finally
        {
            lock (sync) operationInFlight = false;
            operationGate.Release();
        }
    }

    public async Task<CommittedDirectionTransaction> EndAsync(
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Settle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            OpenBeat ending;
            lock (sync)
            {
                if (open is null)
                    throw new DirectionBeatException("No Direction Beat is open.");
                if (open.Directives.Count == 0)
                    throw new DirectionBeatException($"Beat '{open.Id}' contains no directives.");
                operationInFlight = true;
                ending = open;
                open = null;
            }

            var transaction = await transactions.ExecuteAsync(
                PrepareDirectives(ending.Directives),
                completion,
                cancellationToken);
            lock (sync) committed[ending.Id] = transaction;
            return transaction;
        }
        finally
        {
            lock (sync) operationInFlight = false;
            operationGate.Release();
        }
    }

    public void AbortOpen()
    {
        lock (sync) open = null;
    }

    public async Task JoinAsync(
        string beatId,
        CancellationToken cancellationToken = default)
    {
        // Joining is a passive wait. It must not occupy the mutation gate,
        // otherwise a concurrent FinishAsync can never settle the transaction.
        var transaction = GetCommitted(beatId);
        await transaction.Settlement.WaitAsync(cancellationToken);
    }

    public async Task FinishAsync(
        string beatId,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            lock (sync) operationInFlight = true;
            await transactions.FinishAsync(GetCommitted(beatId), cancellationToken);
        }
        finally
        {
            lock (sync) operationInFlight = false;
            operationGate.Release();
        }
    }

    public void ValidateNodeBoundary(string nodeId)
    {
        nodeId = RequireName(nodeId, nameof(nodeId));
        OpenBeat? invalid;
        lock (sync)
        {
            invalid = open;
            if (invalid is not null) open = null;
        }
        if (invalid is not null)
        {
            throw new DirectionBeatException(
                $"Beat '{invalid.Id}' opened in Yarn Node '{invalid.NodeId}' cannot cross " +
                $"the boundary at Node '{nodeId}'.");
        }
    }

    private CommittedDirectionTransaction GetCommitted(string beatId)
    {
        beatId = RequireName(beatId, nameof(beatId));
        CommittedDirectionTransaction? transaction;
        lock (sync) committed.TryGetValue(beatId, out transaction);
        if (transaction is null)
            throw new DirectionBeatException($"Beat '{beatId}' has not been committed.");
        return transaction;
    }

    private IDirectionDirective[] PrepareDirectives(
        IEnumerable<IDirectionDirective> directives)
    {
        var captured = directives.ToArray();
        cues?.Validate(captured.OfType<ITransientDirectionCue>());
        return captured;
    }

    private static string RequireName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public void Dispose() => operationGate.Dispose();

    private sealed record OpenBeat(
        string Id,
        string NodeId,
        DirectionBeatDefaults? Defaults,
        List<IDirectionDirective> Directives);
}
