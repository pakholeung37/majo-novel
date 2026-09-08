#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue.Yarn;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Execution;

public interface ISceneBoundaryCoordinator : IRuntimeService
{
    Task EnterNodeAsync(NodeEntryRequest entry, CancellationToken cancellationToken = default);
    Task EndPresentationAsync(CancellationToken cancellationToken = default);
}

/// <summary>Only explicit scene entries end an owned presentation. Ordinary nodes inherit the current scene.</summary>
public sealed class SceneBoundaryCoordinator : ISceneBoundaryCoordinator, IDisposable
{
    private readonly IDirectionTransactionCoordinator direction;
    private readonly IDirectionBeatRuntime beats;
    private readonly IAudioManager audio;
    private readonly ScenePreparationRegistry? preparation;
    private readonly SemaphoreSlim boundary = new(1, 1);
    private long epoch;

    public SceneBoundaryCoordinator(IDirectionTransactionCoordinator direction, IDirectionBeatRuntime beats, IAudioManager audio, ScenePreparationRegistry? preparation = null)
    { this.direction = direction; this.beats = beats; this.audio = audio; this.preparation = preparation; }

    public Task EnterNodeAsync(NodeEntryRequest entry, CancellationToken cancellationToken = default)
    {
        beats.ValidateNodeBoundary(entry.NodeId);
        if (entry.Reason != NodeEntryReason.Normal || entry.Module?.SceneEntry is not { } scene)
            return Task.CompletedTask;
        return ReplaceSceneAsync(entry.ProjectId, scene.SceneId, cancellationToken);
    }

    public Task EndPresentationAsync(CancellationToken cancellationToken = default)
    {
        beats.ValidateNodeBoundary("host-end-presentation");
        return ReplaceSceneAsync(null, null, cancellationToken);
    }

    private async Task ReplaceSceneAsync(string? projectId, string? sceneId, CancellationToken cancellationToken)
    {
        await boundary.WaitAsync(cancellationToken);
        try
        {
            audio.CloseSceneRequests();
            var nextEpoch = Math.Max(epoch, direction.State.Scene.Epoch) + 1;
            await direction.ClearSceneAsync(new DirectionSceneContext(projectId, sceneId, nextEpoch),
                token => audio.StopBgmAsync(fade: default, cancellationToken: token), CancellationToken.None);
            epoch = nextEpoch;
            if (preparation is not null)
                await preparation.PrepareAsync(projectId, sceneId, ScenePreparationReason.Enter, cancellationToken);
            audio.OpenSceneRequests();
        }
        finally { boundary.Release(); }
    }

    public void Dispose() => boundary.Dispose();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) { epoch++; audio.OpenSceneRequests(); return Task.CompletedTask; }
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
