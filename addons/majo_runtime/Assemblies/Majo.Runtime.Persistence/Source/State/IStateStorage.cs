#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.State;

public interface IStateStorage : IRuntimeService
{
    Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default);

    Task WriteAsync(
        string key,
        System.ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    Task IRuntimeService.InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IRuntimeService.ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IRuntimeService.ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public enum StateStorageCommitStage
{
    TemporaryFileFlushed,
    Committed,
}

public interface IStateStorageCommitObserver
{
    ValueTask ObserveAsync(
        string key,
        StateStorageCommitStage stage,
        CancellationToken cancellationToken);
}

public interface IStateStorageDiagnostics
{
    string? LastKey { get; }
    StateStorageCommitStage? LastCommitStage { get; }
    string? LastFailureMessage { get; }
}

public sealed record StateStorageRecoveryResult(
    string OriginalKey,
    string QuarantineKey,
    string? BackupKey);

/// <summary>
/// Optional recovery capability used by soft-failure scopes. Implementations must move the
/// corrupt object out of the live key instead of deleting or overwriting it.
/// </summary>
public interface IRecoverableStateStorage
{
    Task<StateStorageRecoveryResult> QuarantineAsync(
        string key,
        CancellationToken cancellationToken = default);

    string? GetBackupKey(string key);
}
