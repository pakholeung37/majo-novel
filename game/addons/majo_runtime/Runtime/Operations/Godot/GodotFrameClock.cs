#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Majo.Runtime.Operations.Godot;

public sealed class GodotFrameClock : IFrameClock
{
    private readonly SceneTree sceneTree;
    private readonly CancellationTokenSource serviceCancellation = new();
    private bool isInitialized;

    public GodotFrameClock(SceneTree sceneTree)
    {
        this.sceneTree = sceneTree ?? throw new ArgumentNullException(nameof(sceneTree));
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GodotObject.IsInstanceValid(sceneTree))
        {
            throw new InvalidOperationException("Godot SceneTree is not valid.");
        }

        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized)
        {
            return Task.CompletedTask;
        }

        isInitialized = false;
        serviceCancellation.Cancel();
        return Task.CompletedTask;
    }

    public async Task NextFrameAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            serviceCancellation.Token);
        linked.Token.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProcessFrame() => completion.TrySetResult();

        sceneTree.ProcessFrame += OnProcessFrame;
        using var registration = linked.Token.Register(
            () => completion.TrySetCanceled(linked.Token));

        try
        {
            await completion.Task;
        }
        finally
        {
            if (GodotObject.IsInstanceValid(sceneTree))
            {
                sceneTree.ProcessFrame -= OnProcessFrame;
            }
        }
    }

    public async Task DelayAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration cannot be negative.");
        }

        EnsureInitialized();
        if (duration == TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var durationMicroseconds = duration.TotalMilliseconds * 1_000;
        var startedAtMicroseconds = Time.GetTicksUsec();
        while (Time.GetTicksUsec() - startedAtMicroseconds < durationMicroseconds)
        {
            await NextFrameAsync(cancellationToken);
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
        {
            throw new InvalidOperationException("GodotFrameClock is not initialized.");
        }
    }
}
