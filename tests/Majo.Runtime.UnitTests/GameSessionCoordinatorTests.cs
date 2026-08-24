using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.History;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class GameSessionCoordinatorTests
{
    [Fact]
    public async Task LoadAndRollbackShareTransactionButUseIndependentSessionPolicies()
    {
        var sessions = new RecordingSessions();
        var state = new RecordingStateManager();
        var history = new RecordingHistory();
        var playback = new RecordingPlayback();
        state.Transaction.OnExecute = () =>
            state.Transaction.SawRestorePause = playback.PauseReasons.Contains(
                PlaybackPauseReason.StateRestore);
        var coordinator = new GameSessionCoordinator(sessions, state, history, playback);
        await coordinator.InitializeAsync(CancellationToken.None);
        var snapshot = new StateSnapshot(1, DateTimeOffset.UtcNow, []);
        history.AddEntry();
        playback.SetMode(PlaybackMode.Auto);

        await coordinator.LoadGameAsync(snapshot);

        Assert.Equal(1, sessions.BeginCount);
        Assert.Empty(history.Entries);
        Assert.Equal(PlaybackMode.Manual, playback.Mode);
        Assert.Equal(StateOperationKind.Restore, state.Transaction.PreflightOperations[^1]);
        Assert.Equal(1, state.Transaction.ExecuteCount);
        Assert.False(state.Transaction.SawRestorePause);

        history.AddEntry();
        playback.SetMode(PlaybackMode.Auto);
        await coordinator.RollbackAsync(snapshot);

        Assert.Equal(1, sessions.BeginCount);
        Assert.Single(history.Entries);
        Assert.Equal(PlaybackMode.Manual, playback.Mode);
        Assert.Equal(StateOperationKind.Rollback, state.Transaction.PreflightOperations[^1]);
        Assert.Equal(2, state.Transaction.ExecuteCount);
        Assert.True(state.Transaction.SawRestorePause);
    }

    private sealed class RecordingSessions : IRuntimeSessionController
    {
        public string SessionId { get; private set; } = "session-0";
        public int BeginCount { get; private set; }
        public string BeginSession()
        {
            SessionId = $"session-{++BeginCount}";
            return SessionId;
        }
    }

    private sealed class RecordingStateManager : IStateManager
    {
        public event Action<StateStatusChanged>? StatusChanged { add { } remove { } }
        public event Action<StateSnapshot>? Captured { add { } remove { } }
        public event Action<StateSnapshot>? Restored { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public StateManagerStatus Status => StateManagerStatus.Idle;
        public StateSnapshot? CurrentSnapshot => null;
        public RecordingTransaction Transaction { get; } = new();
        public IStateRestoreTransaction RestoreTransaction => Transaction;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StateSnapshot> CaptureAsync(
            StateScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StateSnapshot> CaptureGameAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingTransaction : IStateRestoreTransaction
    {
        public List<StateOperationKind> PreflightOperations { get; } = [];
        public int ExecuteCount { get; private set; }
        public bool SawRestorePause { get; set; }
        public Action? OnExecute { get; set; }

        public Task<StateRestorePlan> PreflightAsync(
            StateSnapshot snapshot,
            StateScope scope,
            StateOperationKind operation = StateOperationKind.Restore,
            CancellationToken cancellationToken = default)
        {
            PreflightOperations.Add(operation);
            return Task.FromResult(new StateRestorePlan(
                snapshot,
                scope,
                operation,
                new Dictionary<string, StateRecord>()));
        }

        public Task ExecuteAsync(
            StateRestorePlan plan,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            OnExecute?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHistory : IHistoryManager
    {
        private readonly List<HistoryEntry> entries = [];
        public event Action<HistoryOpenStateChanged>? OpenStateChanged { add { } remove { } }
        public event Action<HistoryEntriesChanged>? EntriesChanged { add { } remove { } }
        public IReadOnlyList<HistoryEntry> Entries => entries;
        public int Capacity => 20;
        public bool IsOpen { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void Toggle() => IsOpen = !IsOpen;
        public void Clear() => entries.Clear();
        public Task<bool> ReplayVoiceAsync(
            long sequence,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void AddEntry() => entries.Add(new HistoryEntry(1, "line", null, "text", null));
    }

    private sealed class RecordingPlayback : IPlaybackModeManager
    {
        private readonly List<PlaybackPauseReason> pauses = [];
        public event Action<PlaybackModeChanged>? ModeChanged { add { } remove { } }
        public event Action<PlaybackPauseStateChanged>? PauseChanged { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public PlaybackMode Mode { get; private set; }
        public bool IsPaused => pauses.Count > 0;
        public IReadOnlyList<PlaybackPauseReason> PauseReasons => pauses;
        public TimeSpan ExtraDelay => TimeSpan.Zero;
        public bool WaitForVoice => false;
        public bool HasPendingDeadline => false;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetMode(PlaybackMode mode) => Mode = mode;
        public void ToggleAuto() => SetMode(
            Mode == PlaybackMode.Auto ? PlaybackMode.Manual : PlaybackMode.Auto);
        public IPlaybackPauseHandle AcquirePause(PlaybackPauseReason reason)
        {
            pauses.Add(reason);
            return new PauseHandle(pauses, reason);
        }

        private sealed class PauseHandle(
            ICollection<PlaybackPauseReason> pauses,
            PlaybackPauseReason reason) : IPlaybackPauseHandle
        {
            public PlaybackPauseReason Reason { get; } = reason;
            public bool IsDisposed { get; private set; }
            public void Dispose()
            {
                if (IsDisposed)
                {
                    return;
                }
                pauses.Remove(Reason);
                IsDisposed = true;
            }
        }
    }
}
