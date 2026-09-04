#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Direction.Camera;

public interface ICameraModifierManager : IRuntimeService
{
    bool Contains(string effectName);

    IOperationHandle ApplyEffect(CameraFxCue cue);
}

public interface ICameraModifierRegistry
{
    void Register(ICameraModifier modifier, bool replace = false);
    bool TryGet(string effectName, out ICameraModifier? modifier);
    bool Contains(string effectName);
    void Seal();
}

public sealed class CameraModifierRegistry : ICameraModifierRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, ICameraModifier> modifiers =
        new(StringComparer.OrdinalIgnoreCase);
    private bool isSealed;

    public CameraModifierRegistry(bool registerDefaults = true)
    {
        if (!registerDefaults) return;
        Register(new ShakeCameraModifier());
        Register(new PunchCameraModifier());
        Register(new ZoomPulseCameraModifier("zoom_pulse"));
        Register(new ZoomPulseCameraModifier("pulse"));
    }

    public void Register(ICameraModifier modifier, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(modifier.EffectName);
        lock (gate)
        {
            if (isSealed)
                throw new InvalidOperationException("Camera modifier registry is sealed.");
            if (!replace && modifiers.ContainsKey(modifier.EffectName))
                throw new InvalidOperationException(
                    $"Camera modifier '{modifier.EffectName}' is already registered.");
            modifiers[modifier.EffectName] = modifier;
        }
    }

    public bool TryGet(string effectName, out ICameraModifier? modifier)
    {
        lock (gate) return modifiers.TryGetValue(effectName, out modifier);
    }

    public bool Contains(string effectName)
    {
        lock (gate) return modifiers.ContainsKey(effectName);
    }

    public void Seal()
    {
        lock (gate) isSealed = true;
    }
}

public sealed class CameraModifierManager : ICameraModifierManager
{
    private readonly Action<ViewId, CameraPoseDelta> applyDeltaCallback;
    private readonly Action<ViewId> clearDeltaCallback;
    private readonly IOperationRunner operations;
    private readonly IStageAccessibility accessibility;
    private readonly IFrameClock? frameClock;
    private readonly object gate = new();
    private readonly ICameraModifierRegistry registry;
    private readonly Dictionary<ViewId, Dictionary<Guid, CameraPoseDelta>> activeDeltas = new();
    private readonly Dictionary<ViewId, Dictionary<Guid, IOperationHandle>> activeOperations = new();
    private bool isInitialized;

    public CameraModifierManager(
        Action<ViewId, CameraPoseDelta> applyDeltaCallback,
        Action<ViewId> clearDeltaCallback,
        IOperationRunner operations,
        IStageAccessibility accessibility,
        IFrameClock? frameClock = null,
        bool registerDefaults = true,
        ICameraModifierRegistry? registry = null)
    {
        this.applyDeltaCallback = applyDeltaCallback ?? throw new ArgumentNullException(nameof(applyDeltaCallback));
        this.clearDeltaCallback = clearDeltaCallback ?? throw new ArgumentNullException(nameof(clearDeltaCallback));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
        this.frameClock = frameClock;

        this.registry = registry ?? new CameraModifierRegistry(registerDefaults);
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
        ViewId[] affectedViews;
        lock (gate)
        {
            snapshot = activeOperations.Values.SelectMany(value => value.Values).ToArray();
            affectedViews = activeOperations.Keys.ToArray();
            activeOperations.Clear();
            activeDeltas.Clear();
        }

        foreach (var handle in snapshot) handle.Cancel();
        foreach (var viewId in affectedViews)
            clearDeltaCallback(viewId);
        await AwaitTerminationAsync(snapshot, cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized) return;
        await ResetAsync(cancellationToken);
        isInitialized = false;
    }

    public IOperationHandle ApplyEffect(CameraFxCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        EnsureInitialized();

        if (!registry.TryGet(cue.EffectName, out var modifier) || modifier is null)
        {
            throw new DirectionException($"Unknown camera modifier effect: '{cue.EffectName}'.");
        }

        modifier.Validate(cue.Parameters, cue.DurationSeconds);
        var instanceId = Guid.NewGuid();
        async Task ExecuteAsync(OperationContext context)
        {
            try
            {
                if (accessibility.ReduceMotion)
                    await context.FrameClock.DelayAsync(
                        TimeSpan.FromSeconds(cue.DurationSeconds), context.CancellationToken);
                else
                    await modifier.ExecuteAsync(
                        cue.ViewId, cue.Parameters, cue.DurationSeconds,
                        delta => UpdateDelta(cue.ViewId, instanceId, delta),
                        frameClock ?? context.FrameClock, context.CancellationToken);
            }
            finally
            {
                RemoveDelta(cue.ViewId, instanceId);
            }
        }

        var handle = operations.Start(new OperationDefinition(ExecuteAsync));

        lock (gate)
        {
            if (!activeOperations.TryGetValue(cue.ViewId, out var viewOperations))
                activeOperations[cue.ViewId] = viewOperations = new();
            viewOperations[instanceId] = handle;
        }

        _ = RemoveWhenDoneAsync(cue.ViewId, instanceId, handle);
        return handle;
    }

    public bool Contains(string effectName) => registry.Contains(effectName);

    private async Task RemoveWhenDoneAsync(ViewId viewId, Guid instanceId, IOperationHandle handle)
    {
        try { await handle.Completion; }
        catch { }
        lock (gate)
        {
            if (activeOperations.TryGetValue(viewId, out var viewOperations) &&
                viewOperations.TryGetValue(instanceId, out var current) &&
                ReferenceEquals(current, handle))
            {
                viewOperations.Remove(instanceId);
                if (viewOperations.Count == 0) activeOperations.Remove(viewId);
            }
        }
    }

    private void UpdateDelta(ViewId viewId, Guid instanceId, CameraPoseDelta delta)
    {
        lock (gate)
        {
            if (!activeDeltas.TryGetValue(viewId, out var viewDeltas))
                activeDeltas[viewId] = viewDeltas = new();
            viewDeltas[instanceId] = delta;
            applyDeltaCallback(viewId, viewDeltas.Values.Aggregate(
                CameraPoseDelta.Identity, (current, value) => current.Combine(value)));
        }
    }

    private void RemoveDelta(ViewId viewId, Guid instanceId)
    {
        lock (gate)
        {
            if (!activeDeltas.TryGetValue(viewId, out var viewDeltas))
            {
                clearDeltaCallback(viewId);
                return;
            }
            if (!viewDeltas.Remove(instanceId)) return;
            if (viewDeltas.Count == 0)
            {
                activeDeltas.Remove(viewId);
                clearDeltaCallback(viewId);
                return;
            }
            applyDeltaCallback(viewId, viewDeltas.Values.Aggregate(
                CameraPoseDelta.Identity, (current, value) => current.Combine(value)));
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

    private void EnsureInitialized()
    {
        if (!isInitialized)
            throw new DirectionException("Camera modifier manager is not initialized.");
    }
}
