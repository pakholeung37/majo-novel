using Majo.Runtime.Audio;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Dialogue.Yarn.HotReload;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.UnitTests.Testing;
using Xunit;

namespace Majo.Runtime.UnitTests;

public sealed class SceneLifecycleTests
{
    [Fact(Timeout = 5000)]
    public async Task SceneStopInvalidatesLateLoadsAndStopsEveryBgmTrack()
    {
        var backend = new FakeAudioBackend { AutoCompleteFades = false };
        var loader = new DelayedLoader();
        var audio = new AudioManager(backend, loader, loader, loader, new AudioSettings());
        await audio.InitializeAsync(CancellationToken.None);
        await audio.PlayBgmAsync("old", "music");
        await audio.PlayBgmAsync("ambience", "ambient");
        var fading = await audio.PlayBgmAsync("new", "music", fade: new AudioFade(1));
        var pending = audio.PlayBgmAsync("late", "late");
        Assert.False(pending.IsCompleted);
        audio.CloseSceneRequests();
        await audio.StopBgmAsync();
        audio.OpenSceneRequests();
        loader.Pending.SetResult(new FakeAudioClip("late"));
        await pending;
        await fading.Settlement;
        Assert.Empty(audio.BgmTracks);
        Assert.Empty(backend.ActivePlayers);
        Assert.DoesNotContain(backend.Players, player => player.IsPlaying);
        await audio.PlayBgmAsync("next", "music");
        Assert.Single(audio.BgmTracks);
        await audio.ShutdownAsync(CancellationToken.None);
    }

    [Fact(Timeout = 5000)]
    public async Task ExplicitEntriesRepeatButOrdinaryNodesAndRestorePreserveScene()
    {
        using var direction = new DirectionTransactionCoordinator(new DirectionPlanner(), new FakeDirectionBackend { AutoSettle = true });
        var beats = new DirectionBeatRuntime(direction);
        var loader = new DelayedLoader();
        var audio = new AudioManager(new FakeAudioBackend(), loader, loader, loader, new AudioSettings());
        await audio.InitializeAsync(CancellationToken.None);
        using var boundary = new SceneBoundaryCoordinator(direction, beats, audio);
        var snapshot = new YarnSourceSnapshot("project", "scene.yarnproject", 1, "en",
            new[] { new YarnSourceFileSnapshot("scene.yarn", "scene.yarn",
                "title: Entry\nscene: room\n---\nHello #line:scene-test\n===", "hash") }, "hash");
        var module = NodeDirectionCompiler.Compile(snapshot)[0];
        var entry = new NodeEntryRequest("project", "Entry", 1, module, NodeEntryReason.Normal);
        await boundary.EnterNodeAsync(entry);
        var epoch = direction.State.Scene.Epoch;
        await direction.ExecuteAsync(new[] { ActorPatch.Place(StageId.Main, new ActorId("hero")) });
        await boundary.EnterNodeAsync(entry with { NodeId = "Ordinary", Module = null });
        await boundary.EnterNodeAsync(entry with { Reason = NodeEntryReason.Restore });
        await boundary.EnterNodeAsync(entry with { Reason = NodeEntryReason.InternalRebuild });
        Assert.Single(direction.State.Stages[StageId.Main].Actors);
        Assert.Equal(epoch, direction.State.Scene.Epoch);
        beats.Begin("open", "Entry");
        await Assert.ThrowsAsync<DirectionBeatException>(() => boundary.EnterNodeAsync(entry));
        Assert.Single(direction.State.Stages[StageId.Main].Actors);
        beats.AbortOpen();
        await boundary.EnterNodeAsync(entry);
        Assert.Empty(direction.State.Stages[StageId.Main].Actors);
        Assert.True(direction.State.Scene.Epoch > epoch);
        await boundary.EndPresentationAsync();
        Assert.Null(direction.State.Scene.SceneId);
        await audio.ShutdownAsync(CancellationToken.None);
    }

    private sealed class DelayedLoader : IAudioClipLoader
    {
        public TaskCompletionSource<IAudioClip> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PathPrefix => "audio";
        public bool Exists(string clipId, string? overridePath = null) => true;
        public Task<IAudioClip> LoadAsync(string clipId, string? overridePath = null, CancellationToken cancellationToken = default) =>
            clipId == "late" ? Pending.Task : Task.FromResult<IAudioClip>(new FakeAudioClip(clipId));
    }
}
