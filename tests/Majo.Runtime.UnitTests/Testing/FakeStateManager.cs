#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.State;

namespace Majo.Runtime.UnitTests.Testing;

public sealed class FakeStateManager : IStateManager
{
    public event Action<StateStatusChanged>? StatusChanged { add { } remove { } }
    public event Action<StateSnapshot>? Captured;
    public event Action<StateSnapshot>? Restored;
    public event Action<Exception>? Failed { add { } remove { } }

    public StateManagerStatus Status { get; set; } = StateManagerStatus.Idle;
    public StateSnapshot? CurrentSnapshot { get; set; }
    public IStateRestoreTransaction RestoreTransaction => throw new NotSupportedException();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<StateSnapshot> CaptureAsync(
        StateScope scope,
        CancellationToken cancellationToken = default) => CaptureGameAsync(cancellationToken);

    public Task<StateSnapshot> CaptureGameAsync(
        CancellationToken cancellationToken = default)
    {
        CurrentSnapshot = new StateSnapshot(
            StateSnapshot.CurrentFormatVersion,
            DateTimeOffset.UtcNow,
            []);
        Captured?.Invoke(CurrentSnapshot);
        return Task.FromResult(CurrentSnapshot);
    }

    public StateEnvelope CreateEnvelope(
        StateSnapshot snapshot,
        StateScope scope,
        string runtimeVersion,
        string gameBuildId,
        string storageNamespace,
        IReadOnlyDictionary<string, string>? displayMetadata = null) =>
        throw new NotSupportedException();

    public Task ResetScopeAsync(
        StateScope scope,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RestoreGameAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        CurrentSnapshot = snapshot;
        Restored?.Invoke(snapshot);
        return Task.CompletedTask;
    }
}
