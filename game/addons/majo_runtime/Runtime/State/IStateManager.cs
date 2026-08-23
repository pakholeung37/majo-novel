#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.State;

public interface IStateManager : IRuntimeService
{
    event Action<StateStatusChanged>? StatusChanged;

    event Action<StateSnapshot>? Captured;

    event Action<StateSnapshot>? Restored;

    event Action<Exception>? Failed;

    StateManagerStatus Status { get; }

    StateSnapshot? CurrentSnapshot { get; }

    Task<StateSnapshot> CaptureGameAsync(
        CancellationToken cancellationToken = default);

    Task RestoreGameAsync(
        StateSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
