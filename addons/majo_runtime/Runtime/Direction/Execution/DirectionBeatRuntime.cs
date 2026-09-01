#nullable enable

using System;
using System.Collections.Generic;
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

public sealed class DirectionBeatRuntime : IDirectionBeatRuntime
{
    private readonly IDirectionTransactionCoordinator transactions;
    private readonly Dictionary<string, CommittedDirectionTransaction> committed =
        new(StringComparer.Ordinal);
    private OpenBeat? open;

    public DirectionBeatRuntime(IDirectionTransactionCoordinator transactions)
    {
        this.transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
    }

    public bool HasOpenBeat => open is not null;
    public string? OpenBeatId => open?.Id;
    public DirectionBeatDefaults? OpenBeatDefaults => open?.Defaults;

    public void Begin(
        string beatId,
        string nodeId,
        DirectionBeatDefaults? defaults = null)
    {
        beatId = RequireName(beatId, nameof(beatId));
        nodeId = RequireName(nodeId, nameof(nodeId));
        if (open is not null)
        {
            throw new DirectionBeatException(
                $"Beat '{open.Id}' is already open; Direction Beats cannot nest.");
        }

        if (committed.TryGetValue(beatId, out var existing) &&
            existing.Status == DirectionTransactionStatus.Committed)
        {
            throw new DirectionBeatException(
                $"Beat '{beatId}' already names an unsettled transaction.");
        }

        if (defaults?.DurationSeconds is { } duration &&
            (!double.IsFinite(duration) || duration < 0))
        {
            throw new DirectionBeatException(
                "Direction Beat default duration must be finite and non-negative.");
        }

        open = new OpenBeat(
            beatId,
            nodeId,
            defaults,
            new List<IDirectionDirective>());
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        open = null;
        committed.Clear();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) =>
        ResetAsync(cancellationToken);

    public async Task<CommittedDirectionTransaction?> SubmitAsync(
        IDirectionDirective directive,
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);
        if (open is not null)
        {
            open.Directives.Add(directive);
            return null;
        }

        return await transactions.ExecuteAsync(
            new[] { directive },
            completion,
            cancellationToken);
    }

    public async Task<CommittedDirectionTransaction> EndAsync(
        DirectionCompletionBarrier completion = DirectionCompletionBarrier.Settle,
        CancellationToken cancellationToken = default)
    {
        if (open is null)
            throw new DirectionBeatException("No Direction Beat is open.");
        if (open.Directives.Count == 0)
            throw new DirectionBeatException($"Beat '{open.Id}' contains no directives.");

        var ending = open;
        open = null;
        var transaction = await transactions.ExecuteAsync(
            ending.Directives,
            completion,
            cancellationToken);
        committed[ending.Id] = transaction;
        return transaction;
    }

    public void AbortOpen()
    {
        if (open is null)
            throw new DirectionBeatException("No Direction Beat is open.");
        open = null;
    }

    public async Task JoinAsync(
        string beatId,
        CancellationToken cancellationToken = default)
    {
        var transaction = GetCommitted(beatId);
        await transaction.Settlement.WaitAsync(cancellationToken);
    }

    public Task FinishAsync(
        string beatId,
        CancellationToken cancellationToken = default) =>
        transactions.FinishAsync(GetCommitted(beatId), cancellationToken);

    public void ValidateNodeBoundary(string nodeId)
    {
        nodeId = RequireName(nodeId, nameof(nodeId));
        if (open is not null)
        {
            throw new DirectionBeatException(
                $"Beat '{open.Id}' opened in Yarn Node '{open.NodeId}' cannot cross " +
                $"the boundary at Node '{nodeId}'.");
        }
    }

    private CommittedDirectionTransaction GetCommitted(string beatId)
    {
        beatId = RequireName(beatId, nameof(beatId));
        if (!committed.TryGetValue(beatId, out var transaction))
            throw new DirectionBeatException($"Beat '{beatId}' has not been committed.");
        return transaction;
    }

    private static string RequireName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private sealed record OpenBeat(
        string Id,
        string NodeId,
        DirectionBeatDefaults? Defaults,
        List<IDirectionDirective> Directives);
}
