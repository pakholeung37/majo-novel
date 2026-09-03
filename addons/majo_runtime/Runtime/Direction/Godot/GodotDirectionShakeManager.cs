#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionShakeManager : IDirectionShakeManager
{
    private readonly GodotDirectionRenderHost host;
    private readonly IOperationScheduler operations;
    private readonly IStageAccessibility accessibility;
    private readonly object gate = new();
    private readonly Dictionary<ViewId, IOperationHandle> active = new();
    private bool isInitialized;

    public GodotDirectionShakeManager(
        GodotDirectionRenderHost host,
        IOperationScheduler operations,
        IStageAccessibility accessibility)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IOperationHandle[] snapshot;
        lock (gate)
        {
            snapshot = active.Values.ToArray();
            active.Clear();
        }
        foreach (var handle in snapshot) handle.Cancel();
        foreach (var viewId in host.ViewIds.ToArray()) host.ClearViewShake(viewId);
        await AwaitTerminationAsync(snapshot, cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized) return;
        await ResetAsync(cancellationToken);
        isInitialized = false;
    }

    public IOperationHandle Shake(ShakeCue cue)
    {
        EnsureInitialized();
        Validate(cue);
        host.RequireView(cue.ViewId);

        lock (gate)
        {
            if (active.TryGetValue(cue.ViewId, out var previous))
                previous.Complete();
        }

        IOperationHandle? handle = null;
        handle = operations.Start(new OperationDefinition(
            context => accessibility.ReduceMotion
                ? context.FrameClock.DelayAsync(
                    TimeSpan.FromSeconds(cue.DurationSeconds),
                    context.CancellationToken)
                : host.ShakeViewAsync(cue, context.CancellationToken)));
        lock (gate) active[cue.ViewId] = handle;
        _ = RemoveWhenDoneAsync(cue.ViewId, handle);
        return handle;
    }

    private async Task RemoveWhenDoneAsync(ViewId viewId, IOperationHandle handle)
    {
        try { await handle.Completion; }
        catch { }
        lock (gate)
        {
            if (active.TryGetValue(viewId, out var current) && ReferenceEquals(current, handle))
                active.Remove(viewId);
        }
    }

    private static async Task AwaitTerminationAsync(
        IEnumerable<IOperationHandle> handles,
        CancellationToken cancellationToken)
    {
        foreach (var handle in handles)
        {
            try { await handle.Completion.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static void Validate(ShakeCue cue)
    {
        if (!float.IsFinite(cue.Strength) || cue.Strength < 0 ||
            cue.Frequency <= 0 || !double.IsFinite(cue.DurationSeconds) ||
            cue.DurationSeconds < 0)
        {
            throw new DirectionException("Shake cue has invalid strength, frequency, or duration.");
        }
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
            throw new DirectionException("Direction shake manager is not initialized.");
    }
}
