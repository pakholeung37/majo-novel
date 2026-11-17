#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

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
