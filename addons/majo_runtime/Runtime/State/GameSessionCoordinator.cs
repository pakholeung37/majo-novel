#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.History;

namespace Majo.Runtime.State;

public enum GameSessionTransition
{
    NewGame,
    LoadGame,
    ReturnToTitle,
    Rollback,
}

public sealed record GameSessionChanged(
    GameSessionTransition Transition,
    string SessionId,
    bool BeganNewSession);

public interface IGameSessionCoordinator : IRuntimeService
{
    event Action<GameSessionChanged>? SessionChanged;

    Task NewGameAsync(CancellationToken cancellationToken = default);
    Task LoadGameAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default);
    Task LoadGameAsync(
        StateRestorePlan plan,
        CancellationToken cancellationToken = default);
    Task ReturnToTitleAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

public sealed class GameSessionCoordinator : IGameSessionCoordinator, IDisposable
{
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly IRuntimeSessionController sessions;
    private readonly IStateManager state;
    private readonly IHistoryManager history;
    private readonly IPlaybackModeManager playback;
    private readonly IStateEligibility? eligibility;
    private bool isInitialized;

    public GameSessionCoordinator(
        IRuntimeSessionController sessions,
        IStateManager state,
        IHistoryManager history,
        IPlaybackModeManager playback,
        IStateEligibility? eligibility = null)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.eligibility = eligibility;
    }

    public event Action<GameSessionChanged>? SessionChanged;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public Task NewGameAsync(CancellationToken cancellationToken = default) =>
        ResetGameAsync(GameSessionTransition.NewGame, cancellationToken);

    public Task ReturnToTitleAsync(CancellationToken cancellationToken = default) =>
        ResetGameAsync(GameSessionTransition.ReturnToTitle, cancellationToken);

    public async Task LoadGameAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureInitialized();
        await transitions.WaitAsync(cancellationToken);
        try
        {
            var plan = await state.RestoreTransaction.PreflightAsync(
                snapshot,
                StateScope.Game,
                StateOperationKind.Restore,
                cancellationToken);
            await ExecuteLoadPlanAsync(plan, cancellationToken);
        }
        finally
        {
            transitions.Release();
        }
    }

    public async Task LoadGameAsync(
        StateRestorePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureInitialized();
        if (plan.Scope != StateScope.Game || plan.Operation != StateOperationKind.Restore)
        {
            throw new StateCompatibilityException(
                "Game load requires a Game-scope Restore plan.");
        }
        await transitions.WaitAsync(cancellationToken);
        try
        {
            await ExecuteLoadPlanAsync(plan, cancellationToken);
        }
        finally
        {
            transitions.Release();
        }
    }

    public async Task RollbackAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureInitialized();
        await transitions.WaitAsync(cancellationToken);
        try
        {
            var plan = await state.RestoreTransaction.PreflightAsync(
                snapshot,
                StateScope.Game,
                StateOperationKind.Rollback,
                cancellationToken);
            playback.SetMode(PlaybackMode.Manual);
            using var pause = playback.AcquirePause(PlaybackPauseReason.StateRestore);
            await state.RestoreTransaction.ExecuteAsync(plan, cancellationToken);
            SessionChanged?.Invoke(new GameSessionChanged(
                GameSessionTransition.Rollback,
                sessions.SessionId,
                false));
        }
        finally
        {
            transitions.Release();
        }
    }

    private async Task ResetGameAsync(
        GameSessionTransition transition,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await transitions.WaitAsync(cancellationToken);
        try
        {
            var result = eligibility?.Check(StateOperationKind.Restore);
            if (result is { IsAllowed: false })
            {
                throw new StateEligibilityException(result);
            }
            var sessionId = BeginCleanSession();
            await state.ResetScopeAsync(StateScope.Game, cancellationToken);
            SessionChanged?.Invoke(new GameSessionChanged(transition, sessionId, true));
        }
        finally
        {
            transitions.Release();
        }
    }

    private string BeginCleanSession()
    {
        history.Close();
        history.Clear();
        playback.SetMode(PlaybackMode.Manual);
        return sessions.BeginSession();
    }

    private async Task ExecuteLoadPlanAsync(
        StateRestorePlan plan,
        CancellationToken cancellationToken)
    {
        var sessionId = BeginCleanSession();
        await state.RestoreTransaction.ExecuteAsync(plan, cancellationToken);
        SessionChanged?.Invoke(new GameSessionChanged(
            GameSessionTransition.LoadGame,
            sessionId,
            true));
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new StateRuntimeException("Game session coordinator is not initialized.");
        }
    }

    public void Dispose() => transitions.Dispose();
}
