#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Audio;
using Majo.Runtime.Auto;
using Majo.Runtime.Core;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Operations;
using Majo.Runtime.Script;

namespace Majo.Runtime.Lifecycle;

internal sealed class RuntimeExecutionCoordinator : IRuntimeExecutionCoordinator, IRuntimeActivation
{
    private readonly IRuntimeLogger logger;
    private IServiceResolver resolver = null!;
    private IRuntimeLifetime lifetime = null!;
    private bool directionSkipSubscribed;
    private Task directionSkipTask = Task.CompletedTask;

    public RuntimeExecutionCoordinator(IRuntimeLogger? logger) => this.logger = logger ?? NullRuntimeLogger.Instance;

    public IReadOnlyCollection<Type> ManagedResetServices { get; } = new[]
    {
        typeof(IDirectionTransactionCoordinator),
        typeof(GodotDirectionStageBackend), typeof(GodotDirectionRenderHost),
        typeof(IRuntimeActivityRegistry),
    };

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ActivateAsync(IServiceResolver services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        resolver = services;
        lifetime = services.Get<IRuntimeLifetime>();
        SubscribeDirectionSkip();
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        UnsubscribeDirectionSkip();
        await directionSkipTask.WaitAsync(cancellationToken);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var exceptions = new List<Exception>();
        if (resolver.TryGet<IDirectionBeatRuntime>(out var beats) && beats is not null)
            beats.AbortOpen();
        if (resolver.TryGet<IDirectionTransactionCoordinator>(out var direction) &&
            direction is not null)
        {
            try
            {
                // Restore through the backend first so the visible render state
                // is reset as well as the coordinator's durable state.
                await direction.RestoreAsync(
                    Majo.Runtime.Direction.Core.DirectionState.CreateDefault(),
                    cancellationToken);
                await direction.ResetAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                exceptions.Add(new RuntimeLifecycleException(
                    "Direction coordinator failed to reset its render state.",
                    exception));
            }
        }

        // Direction stage trees own Godot nodes below the render host. They
        // must release those trees before the host removes its viewports;
        // the normal service reset order is dependency-first and therefore
        // has the opposite shape for this pair.
        if (resolver.TryGet<GodotDirectionStageBackend>(out var directionStages) &&
            directionStages is not null)
        {
            try
            {
                await directionStages.ResetAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                exceptions.Add(new RuntimeLifecycleException(
                    "Direction stage backend failed to reset.", exception));
            }
        }
        if (resolver.TryGet<GodotDirectionRenderHost>(out var directionRenderHost) &&
            directionRenderHost is not null)
        {
            try
            {
                await directionRenderHost.ResetAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                exceptions.Add(new RuntimeLifecycleException(
                    "Direction render host failed to reset.", exception));
            }
        }

        if (exceptions.Count > 0)
            throw new AggregateException("Direction reset failed.", exceptions);
    }

    public IRetainedRestoreOutput? RetainRestoreOutput() =>
        resolver.TryGet<GodotDirectionRenderHost>(out var renderer) && renderer is not null
            ? renderer.RetainRestoreOutput() : null;

    public Task CancelForRestoreAsync(CancellationToken cancellationToken = default) =>
        CancelForStateReplacementAsync(false, cancellationToken);

    public Task CancelForHotReloadAsync(CancellationToken cancellationToken) =>
        CancelForStateReplacementAsync(true, cancellationToken);

    private async Task CancelForStateReplacementAsync(
        bool hotReload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<Exception>();

        if (hotReload && resolver.TryGet<IScriptPlayback>(out var script) &&
            script?.BoundarySource is IScriptCheckpointReplayBackend replay)
        {
            try { await replay.StopForReplayAsync(cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }
        else if (resolver.TryGet<IDialoguePlayer>(out var dialogue) && dialogue is not null)
        {
            try { await dialogue.StopAsync(cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (resolver.TryGet<IDirectionTransactionCoordinator>(out var direction) && direction is not null)
        {
            try
            {
                if (direction is not IDirectionStateReplacementSource replacement)
                    throw new InvalidOperationException("Direction backend cannot stop execution while retaining output.");
                await replacement.StopExecutionForRestoreAsync(cancellationToken);
            }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (resolver.TryGet<IAudioManager>(out var audio) && audio is not null)
        {
            try
            {
                if (audio is not IAudioReplacementSource replacement)
                    throw new InvalidOperationException("Audio backend cannot stop execution for prepared replacement.");
                await replacement.StopExecutionForRestoreAsync(cancellationToken);
            }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (resolver.TryGet<IOperationRunner>(out var operations) && operations is not null)
        {
            try { await operations.ResetAsync(cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }

        try { await WaitForQuiescenceAsync(cancellationToken); }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count > 0)
        {
            throw new RuntimeLifecycleException(
                "One or more runtime domains failed to quiesce for restore.",
                new AggregateException(failures));
        }
    }

    public Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default) =>
        resolver.TryGet<IRuntimeActivityObserver>(out var activities) && activities is not null
            ? activities.WaitForQuiescenceAsync(cancellationToken) : Task.CompletedTask;

    private void SubscribeDirectionSkip()
    {
        if (directionSkipSubscribed ||
            !resolver.TryGet<IPlaybackModeManager>(out var playback) || playback is null ||
            !resolver.TryGet<IDirectionTransactionCoordinator>(out var direction) || direction is null)
            return;

        playback.ModeChanged += HandlePlaybackModeChanged;
        directionSkipSubscribed = true;
    }

    private void UnsubscribeDirectionSkip()
    {
        if (!directionSkipSubscribed)
            return;
        if (resolver.TryGet<IPlaybackModeManager>(out var playback) && playback is not null)
            playback.ModeChanged -= HandlePlaybackModeChanged;
        directionSkipSubscribed = false;
    }

    private void HandlePlaybackModeChanged(PlaybackModeChanged change)
    {
        if (change.Current is not (PlaybackMode.SkipReadOnly or PlaybackMode.SkipAll) ||
            !resolver.TryGet<IDirectionTransactionCoordinator>(out var direction) || direction is null)
            return;
        var previous = directionSkipTask;
        directionSkipTask = FinishDirectionSkipAfterAsync(
            previous,
            direction,
            lifetime.SessionCancellationToken);
    }

    private async Task FinishDirectionSkipAsync(
        IDirectionTransactionCoordinator direction,
        CancellationToken cancellationToken)
    {
        try { await direction.FinishSkippableAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.Error(
                "core.lifecycle",
                "core.lifecycle.direction_skip_failed",
                $"Direction skip completion failed: {exception.Message}",
                exception);
        }
    }

    private async Task FinishDirectionSkipAfterAsync(
        Task previous,
        IDirectionTransactionCoordinator direction,
        CancellationToken cancellationToken)
    {
        try
        {
            await previous;
        }
        catch (Exception exception)
        {
            logger.Warning(
                "core.lifecycle",
                "core.lifecycle.direction_skip_previous_failed",
                $"A previous Direction skip operation failed: {exception.Message}",
                exception);
        }
        await FinishDirectionSkipAsync(direction, cancellationToken);
    }
}
