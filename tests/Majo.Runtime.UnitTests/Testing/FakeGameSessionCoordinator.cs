#nullable enable

using Majo.Runtime.State;

namespace Majo.Runtime.UnitTests.Testing;

public sealed class FakeGameSessionCoordinator : IGameSessionCoordinator
{
    public event Action<GameSessionChanged>? SessionChanged;

    public StateSnapshot? LastRollback { get; private set; }
    public StateSnapshot? LastLoadedSnapshot { get; private set; }
    public Exception? RollbackFailure { get; set; }
    public Func<Task>? RollbackWork { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task NewGameAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task LoadGameAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        LastLoadedSnapshot = snapshot;
        return Task.CompletedTask;
    }

    public Task LoadGameAsync(
        StateRestorePlan plan,
        CancellationToken cancellationToken = default)
    {
        LastLoadedSnapshot = plan.Snapshot;
        return Task.CompletedTask;
    }

    public Task ReturnToTitleAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task RollbackAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (RollbackFailure is not null)
        {
            throw RollbackFailure;
        }
        LastRollback = snapshot;
        if (RollbackWork is not null) await RollbackWork();
        SessionChanged?.Invoke(new GameSessionChanged(
            GameSessionTransition.Rollback,
            "session",
            false));
    }

    public void BeginNewSession() => SessionChanged?.Invoke(new GameSessionChanged(
        GameSessionTransition.NewGame,
        "new-session",
        true));

    public void BeginLoadedSession() => SessionChanged?.Invoke(new GameSessionChanged(
        GameSessionTransition.LoadGame,
        "loaded-session",
        true));
}
