#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Accessibility;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Direction.Camera;

public interface ICameraModifierManager : IRuntimeService
{
    bool Contains(string effectName);

    IOperationHandle ApplyEffect(CameraFxCue cue);
    Task ClearViewAsync(ViewId viewId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
    private readonly ICameraModifierRegistry registry;
    private readonly Majo.Runtime.Direction.Execution.ModifierOperationRunner<ViewId, CameraPoseDelta> operations;
    private bool initialized;

    public CameraModifierManager(Action<ViewId, CameraPoseDelta> applyDeltaCallback,
        Action<ViewId> clearDeltaCallback, IOperationRunner operations, IDirectionAccessibility accessibility,
        IFrameClock? frameClock = null, bool registerDefaults = true, ICameraModifierRegistry? registry = null)
    {
        this.registry = registry ?? new CameraModifierRegistry(registerDefaults);
        this.operations = new(operations, accessibility, frameClock, CameraPoseDelta.Identity,
            (left, right) => left.Combine(right), (view, delta) =>
            {
                if (delta == CameraPoseDelta.Identity) clearDeltaCallback(view);
                else applyDeltaCallback(view, delta);
            });
    }
    public bool Contains(string effectName) => registry.Contains(effectName);
    public IOperationHandle ApplyEffect(CameraFxCue cue)
    {
        if (!initialized) throw new DirectionException("Camera modifier manager is not initialized.");
        if (!registry.TryGet(cue.EffectName, out var modifier) || modifier is null)
            throw new DirectionException($"Unknown camera modifier effect: '{cue.EffectName}'.");
        modifier.Validate(cue.Parameters, cue.DurationSeconds);
        return operations.Start(cue.ViewId, cue.DurationSeconds, (apply, clock, token) =>
            modifier.ExecuteAsync(cue.ViewId, cue.Parameters, cue.DurationSeconds, apply, clock, token));
    }
    public Task InitializeAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); initialized = true; return Task.CompletedTask; }
    public Task ResetAsync(CancellationToken cancellationToken) => operations.ResetAsync(cancellationToken);
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    { await ResetAsync(cancellationToken); initialized = false; }
    public Task ClearViewAsync(ViewId viewId, CancellationToken cancellationToken = default) =>
        operations.ClearTargetAsync(viewId, cancellationToken);
}
