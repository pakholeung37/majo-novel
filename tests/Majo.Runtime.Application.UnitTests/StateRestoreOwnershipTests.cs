using System.Text.Json;
using Majo.Runtime.Core;
using Majo.Runtime.Input;
using Majo.Runtime.Script;
using Majo.Runtime.State;
using Xunit;

namespace Majo.Runtime.Application.UnitTests;

public sealed class StateRestoreOwnershipTests
{
    [Fact]
    public async Task FailedOutputReleaseIsNotRetriedWhenFaultResourcesAreReleased()
    {
        var released = new List<string>();
        var boundary = new OutputBoundary(released);
        var input = new InputManager(new FakeInputBackend());
        await input.InitializeAsync(default);
        using var manager = new StateManager([], input, boundary);
        await manager.InitializeAsync(default);
        var snapshot = new StateSnapshot(StateSnapshot.CurrentFormatVersion, DateTimeOffset.UtcNow, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreGameAsync(snapshot));
        Assert.Equal(StateManagerStatus.Faulted, manager.Status);
        await manager.ResetAsync(default);

        Assert.Equal(["output"], released);
        Assert.Equal(StateManagerStatus.Idle, manager.Status);
    }

    private sealed class OutputBoundary(List<string> released) : IRuntimeRestoreBoundary, IRuntimeRestoreOutput
    {
        public IRetainedRestoreOutput RetainRestoreOutput() => new OutputLease(released);
        public Task CancelForRestoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task WaitForQuiescenceAsync(CancellationToken token = default) => Task.CompletedTask;
    }

    [Fact]
    public void FailedOutputReleaseStillReleasesInputAndPlaybackExactlyOnce()
    {
        var released = new List<string>();
        var input = new InputLease(released);
        var resources = new StateRestoreResources
        {
            Output = new OutputLease(released),
            InputMuter = input,
            PlaybackLease = new PlaybackLease(released),
        };
        Assert.Throws<AggregateException>(resources.Dispose);
        Assert.True(input.IsDisposed);
        Assert.Equal(["output", "input", "playback"], released);
        resources.Dispose();
        Assert.Equal(3, released.Count);
    }

    [Fact]
    public void ScriptBoundaryCapabilityReconcilesCustomKeyWithoutReadingLivePlayback()
    {
        var contributor = new CustomScriptContributor();
        var capture = new StateSnapshotCapture([contributor]);
        var other = new StateRecord("other", StateScope.Game, 1, JsonSerializer.SerializeToElement(42));
        var snapshot = new StateSnapshot(StateSnapshot.CurrentFormatVersion, DateTimeOffset.UtcNow,
            [other, contributor.CaptureBoundary(Checkpoint("old"))]);
        var result = capture.Reconcile(snapshot, Checkpoint("new"));
        Assert.Same(other, result.Records["other"]);
        Assert.Equal("new", result.Records[contributor.Key].Payload.GetString());
        Assert.Equal(snapshot.CapturedAt, result.CapturedAt);
        Assert.Equal(2, result.Records.Count);
    }

    private static ScriptCheckpoint Checkpoint(string line) => new(ScriptCheckpoint.CurrentFormatVersion,
        "program", new ScriptPlaybackSpot(new ScriptId("project", "Start"), line), []);

    private sealed class CustomScriptContributor : IScriptBoundaryStateContributor
    {
        public string Key => "custom.script";
        public StateScope Scope => StateScope.Game;
        public int SchemaVersion => 1;
        public StateContributorCapability Capability => StateContributorCapability.DurableRollback;
        public StateRecord CaptureBoundary(ScriptCheckpoint checkpoint) => new(Key, Scope, SchemaVersion,
            JsonSerializer.SerializeToElement(checkpoint.Spot.LineId));
        public ValueTask<StateRecord> CaptureAsync(CancellationToken token) => throw new InvalidOperationException("Live capture is forbidden.");
        public ValueTask PreflightAsync(StateRecord record, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ResetAsync(CancellationToken token) => ValueTask.CompletedTask;
    }

    private sealed class OutputLease(List<string> released) : IRetainedRestoreOutput
    {
        public Task WaitForTargetAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { released.Add("output"); throw new InvalidOperationException("output release failed"); }
    }
    private sealed class PlaybackLease(List<string> released) : IDisposable
    {
        public void Dispose() => released.Add("playback");
    }
    private sealed class InputLease(List<string> released) : IInputHandle
    {
        public event Action<InputActivation>? Activated { add { } remove { } }
        public object Owner => this;
        public InputHandleKind Kind => InputHandleKind.Muter;
        public string? Name => "test";
        public bool IsModal => true;
        public bool Enabled { get; set; }
        public bool IsDisposed { get; private set; }
        public void Dispose() { IsDisposed = true; released.Add("input"); }
    }
}
