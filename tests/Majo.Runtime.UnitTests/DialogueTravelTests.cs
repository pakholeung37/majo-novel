using Majo.Runtime.Auto;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Majo.Runtime.Travel;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class DialogueTravelTests
{
    [Fact(Timeout = 5_000)]
    public async Task CapturesStableLinesAndSupportsBackwardForwardAndBranchingTravel()
    {
        var harness = await TravelHarness.CreateAsync(capacity: 4);
        await harness.CaptureLineAsync("line:one", "One");
        await harness.CaptureLineAsync("line:two", "Two");
        await harness.CaptureLineAsync("line:three", "Three");

        Assert.Equal(3, harness.Manager.Entries.Count);
        Assert.Equal(2, harness.Manager.CurrentIndex);
        Assert.True(await harness.Manager.StepBackAsync());
        Assert.Equal(1, harness.Manager.CurrentIndex);
        Assert.Same(harness.Manager.Entries[1].Snapshot, harness.Sessions.LastRollback);
        Assert.True(harness.Manager.CanStepForward);

        Assert.True(await harness.Manager.StepForwardAsync());
        Assert.Equal(2, harness.Manager.CurrentIndex);
        Assert.True(await harness.Manager.StepBackAsync());
        await harness.CaptureLineAsync("line:branch", "Branch");

        Assert.Equal(
            ["line:one", "line:two", "line:branch"],
            harness.Manager.Entries.Select(entry => entry.Spot.LineId));
        Assert.Equal(2, harness.Manager.CurrentIndex);
        Assert.False(harness.Manager.CanStepForward);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task CapacityDropsOldestCheckpointAndRestoreFailureKeepsCursor()
    {
        var harness = await TravelHarness.CreateAsync(capacity: 2);
        await harness.CaptureLineAsync("line:one", "One");
        await harness.CaptureLineAsync("line:two", "Two");
        await harness.CaptureLineAsync("line:three", "Three");

        Assert.Equal(
            ["line:two", "line:three"],
            harness.Manager.Entries.Select(entry => entry.Spot.LineId));
        Assert.Equal(1, harness.Manager.CurrentIndex);

        harness.Sessions.RollbackFailure = new InvalidOperationException("restore failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Manager.StepBackAsync());
        Assert.Equal(1, harness.Manager.CurrentIndex);
        Assert.False(harness.Manager.IsTravelling);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task IgnoresRestoreReplayAndClearsWhenANewSessionBegins()
    {
        var harness = await TravelHarness.CreateAsync(capacity: 4);
        await harness.CaptureLineAsync("line:one", "One");
        await harness.PresentLineAsync(
            "line:restored",
            "Restored",
            DialoguePresentationOrigin.CheckpointRestore);

        Assert.Single(harness.Manager.Entries);
        harness.Sessions.BeginNewSession();
        Assert.Empty(harness.Manager.Entries);
        Assert.Equal(-1, harness.Manager.CurrentIndex);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task LoadStartsANewTimelineAtTheRestoredStableLine()
    {
        var harness = await TravelHarness.CreateAsync(capacity: 4);
        await harness.CaptureLineAsync("line:old", "Old");
        await harness.PresentLineAsync(
            "line:loaded",
            "Loaded",
            DialoguePresentationOrigin.CheckpointRestore);

        harness.Sessions.BeginLoadedSession();
        await harness.WaitForLineAsync("line:loaded");

        Assert.Single(harness.Manager.Entries);
        Assert.Equal("line:loaded", harness.Manager.Entries[0].Spot.LineId);
        Assert.Equal(0, harness.Manager.CurrentIndex);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task HotReloadReplayCanSeedAnEmptyTimeline()
    {
        var harness = await TravelHarness.CreateAsync(capacity: 4);
        await harness.PresentLineAsync(
            "line:reloaded",
            "Reloaded",
            DialoguePresentationOrigin.HotReloadReplay);
        await harness.WaitForLineAsync("line:reloaded");

        Assert.Single(harness.Manager.Entries);
        Assert.Equal(0, harness.Manager.CurrentIndex);
        await harness.ShutdownAsync();
    }

    [Fact(Timeout = 5_000)]
    public async Task ReleasingAnUnrelatedPlaybackPauseDoesNotDuplicateCurrentLine()
    {
        var harness = await TravelHarness.CreateAsync(capacity: 4);
        await harness.CaptureLineAsync("line:one", "One");

        var pause = harness.Playback.AcquirePause(PlaybackPauseReason.External);
        pause.Dispose();
        await Task.Delay(20);

        Assert.Single(harness.Manager.Entries);
        Assert.Equal("line:one", harness.Manager.Entries[0].Spot.LineId);
        await harness.ShutdownAsync();
    }

    private sealed class TravelHarness
    {
        private TravelHarness(
            FakeStateManager state,
            FakeGameSessionCoordinator sessions,
            FakeScriptPlayback script,
            FakeDialoguePlayer dialogue,
            FakePlaybackModeManager playback,
            DialogueTravelManager manager)
        {
            State = state;
            Sessions = sessions;
            Script = script;
            Dialogue = dialogue;
            Playback = playback;
            Manager = manager;
        }

        public FakeStateManager State { get; }
        public FakeGameSessionCoordinator Sessions { get; }
        public FakeScriptPlayback Script { get; }
        public FakeDialoguePlayer Dialogue { get; }
        public FakePlaybackModeManager Playback { get; }
        public DialogueTravelManager Manager { get; }

        public static async Task<TravelHarness> CreateAsync(int capacity)
        {
            var state = new FakeStateManager();
            var sessions = new FakeGameSessionCoordinator();
            var script = new FakeScriptPlayback();
            var dialogue = new FakeDialoguePlayer();
            var playback = new FakePlaybackModeManager();
            var manager = new DialogueTravelManager(
                state,
                sessions,
                script,
                dialogue,
                playback,
                capacity);
            await manager.InitializeAsync(CancellationToken.None);
            return new TravelHarness(
                state,
                sessions,
                script,
                dialogue,
                playback,
                manager);
        }

        public Task CaptureLineAsync(string lineId, string text) =>
            PresentLineAsync(lineId, text, DialoguePresentationOrigin.Normal);

        public async Task PresentLineAsync(
            string lineId,
            string text,
            DialoguePresentationOrigin origin)
        {
            Script.SetPhase(ScriptPlaybackPhase.Executing);
            Script.Spot = new ScriptPlaybackSpot(
                new ScriptId("main", "Start"),
                lineId);
            Dialogue.Present(new DialogueLine(
                lineId,
                "Narrator",
                new DialogueText(text, []),
                [],
                Origin: origin));
            Script.SetPhase(ScriptPlaybackPhase.AwaitingAdvance);
            if (origin == DialoguePresentationOrigin.Normal)
            {
                await WaitUntilAsync(() =>
                    Manager.Entries.LastOrDefault()?.Spot.LineId == lineId);
            }
            else
            {
                await Task.Delay(20);
            }
        }

        public Task ShutdownAsync() => Manager.ShutdownAsync(CancellationToken.None);

        public Task WaitForLineAsync(string lineId) => WaitUntilAsync(() =>
            Manager.Entries.LastOrDefault()?.Spot.LineId == lineId);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            {
                await Task.Delay(5);
            }
            Assert.True(condition());
        }
    }


    private sealed class FakePlaybackModeManager : IPlaybackModeManager
    {
        private readonly List<PlaybackPauseReason> pauses = [];
        public event Action<PlaybackModeChanged>? ModeChanged { add { } remove { } }
        public event Action<PlaybackPauseStateChanged>? PauseChanged;
        public event Action<Exception>? Failed { add { } remove { } }
        public PlaybackMode Mode { get; private set; } = PlaybackMode.Auto;
        public bool IsPaused => pauses.Count > 0;
        public IReadOnlyList<PlaybackPauseReason> PauseReasons => pauses;
        public TimeSpan ExtraDelay => TimeSpan.Zero;
        public bool WaitForVoice => false;
        public bool HasPendingDeadline => false;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetMode(PlaybackMode mode) => Mode = mode;
        public void ToggleAuto() { }
        public IPlaybackPauseHandle AcquirePause(PlaybackPauseReason reason)
        {
            pauses.Add(reason);
            PublishPauseChanged();
            return new PauseHandle(pauses, reason, PublishPauseChanged);
        }

        private void PublishPauseChanged() => PauseChanged?.Invoke(
            new PlaybackPauseStateChanged(pauses.Count > 0, pauses.ToArray()));

        private sealed class PauseHandle(
            ICollection<PlaybackPauseReason> pauses,
            PlaybackPauseReason reason,
            Action changed) : IPlaybackPauseHandle
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
                changed();
            }
        }
    }
}
