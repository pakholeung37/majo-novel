#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Stage;

public sealed class CameraManager : ICameraManager
{
    private readonly object gate = new();
    private readonly IStageBackend backend;
    private readonly IOperationScheduler operations;
    private readonly IStageAccessibility accessibility;
    private readonly Dictionary<string, IOperationHandle> propertyOperations = new(StringComparer.Ordinal);
    private IOperationHandle? viewTransitionOperation;
    private IOperationHandle? shakeOperation;
    private bool isInitialized;

    public CameraManager(
        IStageBackend backend,
        IOperationScheduler operations,
        IStageAccessibility accessibility)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
    }

    public CameraState State { get; private set; } = CameraState.Default;
    public CameraState TargetState { get; private set; } = CameraState.Default;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        backend.ApplyCamera(State);
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelAll();
        State = TargetState = CameraState.Default;
        backend.ClearShake();
        backend.ClearCameraViewTransition();
        backend.ApplyCamera(State);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized) return Task.CompletedTask;
        CancelAll();
        backend.ClearShake();
        backend.ClearCameraViewTransition();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public IOperationHandle MoveTo(Vector2 position, StageOperationSpec? operation = null)
    {
        ValidatePosition(position);
        return PoseTo(new CameraPoseUpdate(Center: position), operation);
    }

    public IOperationHandle ZoomTo(float zoom, StageOperationSpec? operation = null)
    {
        if (!float.IsFinite(zoom) || zoom <= 0) throw new ArgumentOutOfRangeException(nameof(zoom));
        return PoseTo(new CameraPoseUpdate(Zoom: zoom), operation);
    }

    public IOperationHandle RotateTo(float degrees, StageOperationSpec? operation = null)
    {
        if (!float.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
        return PoseTo(new CameraPoseUpdate(RotationDegrees: degrees), operation);
    }

    public IOperationHandle TransitionTo(CameraPoseTransitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return request.Transition.Kind switch
        {
            CameraViewTransitionKind.Cut or CameraViewTransitionKind.Move =>
                PoseTo(request.Target, request.Transition.Operation),
            CameraViewTransitionKind.Dissolve => DissolveTo(
                request.Target,
                request.Transition.Operation),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    public IOperationHandle PoseTo(
        CameraPoseUpdate pose,
        StageOperationSpec? operation = null)
    {
        ArgumentNullException.ThrowIfNull(pose);
        pose.Validate();
        var properties = new List<string>(3);
        if (pose.Center is not null) properties.Add("center");
        if (pose.Zoom is not null) properties.Add("zoom");
        if (pose.RotationDegrees is not null) properties.Add("rotation");
        return StartTransform(
            properties,
            pose.Apply,
            (from, to, spec, token) => backend.AnimateCameraAsync(
                from,
                to,
                new CameraPoseUpdate(
                    properties.Contains("center") ? to.Center : null,
                    properties.Contains("zoom") ? to.Zoom : null,
                    properties.Contains("rotation") ? to.RotationDegrees : null),
                spec,
                token),
            operation);
    }

    public IOperationHandle ResetCamera(CameraViewTransitionSpec transition) => TransitionTo(
        new CameraPoseTransitionRequest(
            new CameraPoseUpdate(
                CameraState.Default.Center,
                CameraState.Default.Zoom,
                CameraState.Default.RotationDegrees),
            transition));

    public IOperationHandle Shake(CameraShakeSpec shake)
    {
        EnsureInitialized();
        shake.Validate();
        var spec = shake.EffectiveOperation.Validate();
        lock (gate) shakeOperation?.Complete();
        IOperationHandle? handle = null;
        var definition = new OperationDefinition(
            async context =>
            {
                if (!accessibility.ReduceMotion)
                    await backend.ShakeAsync(shake, context.CancellationToken);
                else
                    await context.FrameClock.DelayAsync(
                        TimeSpan.FromSeconds(spec.DurationSeconds),
                        context.CancellationToken);
                backend.ClearShake();
            },
            spec.OperationId,
            spec.GroupId,
            _ => { backend.ClearShake(); return Task.CompletedTask; },
            _ => { backend.ClearShake(); return Task.CompletedTask; },
            spec.Skippable);
        handle = operations.Start(definition);
        lock (gate) shakeOperation = handle;
        _ = ClearShakeWhenDoneAsync(handle);
        return handle;
    }

    public void ApplyStableState(CameraState state)
    {
        EnsureInitialized();
        state.Validate();
        CancelAll();
        State = TargetState = state;
        backend.ClearCameraViewTransition();
        backend.ApplyCamera(state);
    }

    private IOperationHandle DissolveTo(
        CameraPoseUpdate pose,
        StageOperationSpec requested)
    {
        EnsureInitialized();
        pose.Validate();
        var spec = requested.Validate();
        IOperationHandle[] predecessors;
        CameraState final;
        lock (gate)
        {
            predecessors = propertyOperations.Values
                .Append(viewTransitionOperation)
                .Where(value => value is not null)
                .Cast<IOperationHandle>()
                .Distinct()
                .ToArray();
            propertyOperations.Clear();
            viewTransitionOperation = null;
            foreach (var predecessor in predecessors) predecessor.Complete();
            shakeOperation?.Complete();
            final = pose.Apply(TargetState).Validate();
            TargetState = final;
        }

        IOperationHandle? handle = null;
        var definition = new OperationDefinition(
            async context =>
            {
                await AwaitPredecessorsAsync(predecessors, context.CancellationToken);
                CameraState from;
                lock (gate) from = State;
                await backend.DissolveCameraAsync(
                    from,
                    final,
                    spec,
                    context.CancellationToken);
                lock (gate) State = final;
            },
            spec.OperationId,
            spec.GroupId,
            _ =>
            {
                backend.ClearCameraViewTransition();
                backend.ApplyCamera(final);
                lock (gate) State = TargetState = final;
                return Task.CompletedTask;
            },
            _ =>
            {
                backend.ClearCameraViewTransition();
                backend.ApplyCamera(final);
                lock (gate) State = TargetState = final;
                return Task.CompletedTask;
            },
            spec.Skippable);
        handle = operations.Start(definition);
        lock (gate) viewTransitionOperation = handle;
        _ = ClearViewTransitionWhenDoneAsync(handle);
        return handle;
    }

    private IOperationHandle StartTransform(
        IReadOnlyList<string> properties,
        Func<CameraState, CameraState> target,
        Func<CameraState, CameraState, StageOperationSpec, CancellationToken, Task> animate,
        StageOperationSpec? requested)
    {
        EnsureInitialized();
        var spec = (requested ?? new StageOperationSpec()).Validate();
        var playbackSpec = accessibility.ReduceMotion && spec.Curve is
                MotionCurve.BackIn or MotionCurve.BackOut or MotionCurve.BackInOut
            ? spec with { Curve = MotionCurve.CubicOut }
            : spec;
        IOperationHandle? viewPredecessor;
        lock (gate) viewPredecessor = viewTransitionOperation;
        viewPredecessor?.Complete();
        IOperationHandle[] predecessors;
        CameraState final;
        lock (gate)
        {
            predecessors = properties.Where(propertyOperations.ContainsKey)
                .Select(property => propertyOperations[property])
                .Append(viewPredecessor)
                .Where(value => value is not null)
                .Cast<IOperationHandle>()
                .Distinct()
                .ToArray();
            foreach (var predecessor in predecessors) predecessor.Cancel();
            final = target(TargetState).Validate();
            TargetState = final;
        }

        IOperationHandle? handle = null;
        var definition = new OperationDefinition(
            async context =>
            {
                await AwaitPredecessorsAsync(predecessors, context.CancellationToken);
                CameraState from;
                lock (gate) from = State;
                await animate(from, final, playbackSpec, context.CancellationToken);
                lock (gate) State = MergeProperties(State, final, properties);
            },
            spec.OperationId,
            spec.GroupId,
            _ =>
            {
                lock (gate) State = MergeProperties(State, final, properties);
                backend.ApplyCameraPose(CreatePoseUpdate(final, properties));
                return Task.CompletedTask;
            },
            _ =>
            {
                var sampled = backend.RenderedCamera;
                lock (gate)
                {
                    State = MergeProperties(State, sampled, properties);
                    TargetState = MergeProperties(TargetState, sampled, properties);
                }
                return Task.CompletedTask;
            },
            spec.Skippable);
        handle = operations.Start(definition);
        lock (gate) foreach (var property in properties) propertyOperations[property] = handle;
        _ = ClearPropertiesWhenDoneAsync(handle, properties);
        return handle;
    }

    private static CameraState MergeProperties(
        CameraState current,
        CameraState source,
        IReadOnlyCollection<string> properties) => current with
        {
            Center = properties.Contains("center") ? source.Center : current.Center,
            Zoom = properties.Contains("zoom") ? source.Zoom : current.Zoom,
            RotationDegrees = properties.Contains("rotation")
            ? source.RotationDegrees
            : current.RotationDegrees,
        };

    private static CameraPoseUpdate CreatePoseUpdate(
        CameraState state,
        IReadOnlyCollection<string> properties) => new(
        properties.Contains("center") ? state.Center : null,
        properties.Contains("zoom") ? state.Zoom : null,
        properties.Contains("rotation") ? state.RotationDegrees : null);

    private async Task ClearPropertiesWhenDoneAsync(
        IOperationHandle handle,
        IReadOnlyCollection<string> properties)
    {
        try { await handle.Completion; } catch { }
        lock (gate)
            foreach (var property in properties)
                if (propertyOperations.TryGetValue(property, out var current) && ReferenceEquals(current, handle))
                    propertyOperations.Remove(property);
    }

    private async Task ClearShakeWhenDoneAsync(IOperationHandle handle)
    {
        try { await handle.Completion; } catch { }
        lock (gate) if (ReferenceEquals(shakeOperation, handle)) shakeOperation = null;
    }

    private async Task ClearViewTransitionWhenDoneAsync(IOperationHandle handle)
    {
        try { await handle.Completion; } catch { }
        lock (gate)
            if (ReferenceEquals(viewTransitionOperation, handle))
                viewTransitionOperation = null;
    }

    private void CancelAll()
    {
        IOperationHandle[] snapshot;
        lock (gate)
        {
            snapshot = propertyOperations.Values.Append(shakeOperation)
                .Append(viewTransitionOperation)
                .Where(value => value is not null).Cast<IOperationHandle>().Distinct().ToArray();
            propertyOperations.Clear();
            viewTransitionOperation = null;
            shakeOperation = null;
        }
        foreach (var operation in snapshot) operation.Cancel();
    }

    private static async Task AwaitPredecessorsAsync(
        IEnumerable<IOperationHandle> predecessors,
        CancellationToken cancellationToken)
    {
        foreach (var predecessor in predecessors)
        {
            try
            {
                await predecessor.Completion.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                predecessor.State == OperationState.Cancelled &&
                !cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static void ValidatePosition(Vector2 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentException("Camera position must be finite.");
    }

    private void EnsureInitialized()
    {
        if (!isInitialized) throw new InvalidOperationException("CameraManager is not initialized.");
    }
}

public sealed class StageTransitionManager : IStageTransitionManager
{
    private readonly object gate = new();
    private readonly IStageBackend backend;
    private readonly IOperationScheduler operations;
    private readonly Dictionary<string, IStageTransitionProvider> providers;
    private IOperationHandle? active;
    private bool isInitialized;

    public StageTransitionManager(
        IStageBackend backend,
        IOperationScheduler operations,
        IEnumerable<IStageTransitionProvider>? providers = null)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.providers = (providers ?? Array.Empty<IStageTransitionProvider>())
            .ToDictionary(provider => provider.Id, StringComparer.Ordinal);
    }

    public string? Appearance { get; private set; }
    public IReadOnlyCollection<string> ProviderIds => providers.Keys.ToArray();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        isInitialized = true;
        return Task.CompletedTask;
    }
    public Task ResetAsync(CancellationToken cancellationToken)
    {
        active?.Cancel();
        active = null;
        Appearance = null;
        backend.ClearTransition();
        return Task.CompletedTask;
    }
    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized) return Task.CompletedTask;
        active?.Cancel();
        active = null;
        backend.ClearTransition();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public IOperationHandle Run(StageTransitionRequest request)
    {
        EnsureInitialized();
        request.Validate();
        IStageTransitionProvider? provider = null;
        if (request.Kind == StageTransitionKind.Custom &&
            !providers.TryGetValue(request.ProviderId!, out provider))
            throw new InvalidOperationException(
                $"Stage transition provider '{request.ProviderId}' is not registered.");
        lock (gate) active?.Complete();
        IOperationHandle? handle = null;
        var definition = new OperationDefinition(
            async context =>
            {
                if (request.Resources is not null)
                    await request.Resources.Completion.WaitAsync(context.CancellationToken);
                if (provider is null)
                    await backend.RunTransitionAsync(request, context.CancellationToken);
                else
                    await provider.RunAsync(backend, request, context.CancellationToken);
                Appearance = request.TargetAppearance ?? Appearance;
                backend.ClearTransition();
            },
            request.Operation.OperationId,
            request.Operation.GroupId,
            _ =>
            {
                if (provider is null) backend.ClearTransition();
                else provider.Complete(backend, request);
                Appearance = request.TargetAppearance ?? Appearance;
                return Task.CompletedTask;
            },
            _ =>
            {
                if (provider is null) backend.ClearTransition();
                else provider.Cancel(backend, request);
                return Task.CompletedTask;
            },
            request.Operation.Skippable);
        handle = operations.Start(definition);
        lock (gate) active = handle;
        _ = ClearWhenDoneAsync(handle);
        return handle;
    }

    public void ApplyStableAppearance(string? appearance)
    {
        EnsureInitialized();
        active?.Cancel();
        backend.ClearTransition();
        Appearance = appearance;
    }

    private async Task ClearWhenDoneAsync(IOperationHandle handle)
    {
        try { await handle.Completion; } catch { }
        lock (gate) if (ReferenceEquals(active, handle)) active = null;
    }
    private void EnsureInitialized()
    {
        if (!isInitialized) throw new InvalidOperationException("StageTransitionManager is not initialized.");
    }
}

public sealed class StageCurtainManager : IStageCurtainManager
{
    private readonly object gate = new();
    private readonly IStageBackend backend;
    private readonly IOperationScheduler operations;
    private IOperationHandle? active;
    private bool isInitialized;

    public StageCurtainManager(
        IStageBackend backend,
        IOperationScheduler operations)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public StageCurtainState State { get; private set; } = StageCurtainState.Default;
    public StageCurtainState TargetState { get; private set; } = StageCurtainState.Default;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        backend.ApplyCurtain(State);
        isInitialized = true;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        active?.Cancel();
        active = null;
        State = TargetState = StageCurtainState.Default;
        backend.ApplyCurtain(State);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized) return Task.CompletedTask;
        active?.Cancel();
        active = null;
        State = TargetState = StageCurtainState.Default;
        isInitialized = false;
        return Task.CompletedTask;
    }

    public IOperationHandle SetCurtain(
        Color color,
        float opacity,
        StageOperationSpec? operation = null)
    {
        EnsureInitialized();
        var target = new StageCurtainState(color, opacity).Validate();
        var spec = (operation ?? new StageOperationSpec()).Validate();
        IOperationHandle? predecessor;
        lock (gate)
        {
            predecessor = active;
            predecessor?.Complete();
            TargetState = target;
        }

        IOperationHandle? handle = null;
        handle = operations.Start(new OperationDefinition(
            async context =>
            {
                if (predecessor is not null)
                    await predecessor.Completion.WaitAsync(context.CancellationToken);
                StageCurtainState from;
                lock (gate) from = State;
                await backend.AnimateCurtainAsync(
                    from, target, spec.DurationSeconds, context.CancellationToken);
                lock (gate) State = target;
            },
            spec.OperationId,
            spec.GroupId,
            _ =>
            {
                lock (gate) State = target;
                backend.ApplyCurtain(target);
                return Task.CompletedTask;
            },
            _ =>
            {
                StageCurtainState stable;
                lock (gate)
                {
                    stable = State;
                    if (ReferenceEquals(active, handle)) TargetState = stable;
                }
                backend.ApplyCurtain(stable);
                return Task.CompletedTask;
            },
            spec.Skippable));
        lock (gate) active = handle;
        _ = ClearWhenDoneAsync(handle);
        return handle;
    }

    public void ApplyStableState(StageCurtainState state)
    {
        EnsureInitialized();
        state.Validate();
        active?.Cancel();
        active = null;
        State = TargetState = state;
        backend.ApplyCurtain(state);
    }

    private async Task ClearWhenDoneAsync(IOperationHandle handle)
    {
        try { await handle.Completion; } catch { }
        lock (gate) if (ReferenceEquals(active, handle)) active = null;
    }

    private void EnsureInitialized()
    {
        if (!isInitialized) throw new InvalidOperationException(
            "StageCurtainManager is not initialized.");
    }
}

public sealed class ScreenEffectManager : IScreenEffectManager
{
    private readonly object gate = new();
    private readonly IStageBackend backend;
    private readonly IOperationScheduler operations;
    private readonly IStageAccessibility accessibility;
    private readonly Dictionary<string, IOperationHandle> propertyOperations = new(StringComparer.Ordinal);
    private IOperationHandle? flashOperation;
    private bool isInitialized;

    public ScreenEffectManager(
        IStageBackend backend,
        IOperationScheduler operations,
        IStageAccessibility accessibility)
    {
        this.backend = backend;
        this.operations = operations;
        this.accessibility = accessibility;
    }

    public ScreenEffectState State { get; private set; } = ScreenEffectState.Default;
    public ScreenEffectState TargetState { get; private set; } = ScreenEffectState.Default;
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        backend.ApplyScreenEffect(State);
        isInitialized = true;
        return Task.CompletedTask;
    }
    public Task ResetAsync(CancellationToken cancellationToken)
    {
        CancelAll();
        State = TargetState = ScreenEffectState.Default;
        backend.ClearFlash();
        backend.ApplyScreenEffect(State);
        return Task.CompletedTask;
    }
    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!isInitialized) return Task.CompletedTask;
        CancelAll();
        backend.ClearFlash();
        isInitialized = false;
        return Task.CompletedTask;
    }

    public IOperationHandle SetOverlay(Color color, float opacity, StageOperationSpec? operation = null)
    {
        if (!float.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        return StartPersistent(
            "overlay",
            state => state with { OverlayColor = color, OverlayOpacity = opacity },
            (from, to, duration, token) => backend.AnimateOverlayAsync(from, to, duration, token),
            operation);
    }

    public IOperationHandle SetLetterbox(float ratio, StageOperationSpec? operation = null)
    {
        if (!float.IsFinite(ratio) || ratio is < 0 or > 0.5f)
            throw new ArgumentOutOfRangeException(nameof(ratio));
        return StartPersistent(
            "letterbox",
            state => state with { LetterboxRatio = ratio },
            (from, to, duration, token) => backend.AnimateLetterboxAsync(
                from.LetterboxRatio, to.LetterboxRatio, duration, token),
            operation);
    }

    public IOperationHandle Flash(Color color, float intensity = 1, StageOperationSpec? operation = null)
    {
        EnsureInitialized();
        if (!float.IsFinite(intensity) || intensity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(intensity));
        var spec = (operation ?? new StageOperationSpec(0.2)).Validate();
        lock (gate) flashOperation?.Complete();
        IOperationHandle? handle = null;
        handle = operations.Start(new OperationDefinition(
            async context =>
            {
                if (!accessibility.ReduceMotion)
                    await backend.FlashAsync(color, intensity, spec.DurationSeconds, context.CancellationToken);
                backend.ClearFlash();
            },
            spec.OperationId,
            spec.GroupId,
            _ => { backend.ClearFlash(); return Task.CompletedTask; },
            _ => { backend.ClearFlash(); return Task.CompletedTask; },
            spec.Skippable));
        lock (gate) flashOperation = handle;
        _ = ClearFlashWhenDoneAsync(handle);
        return handle;
    }

    public void ApplyStableState(ScreenEffectState state)
    {
        EnsureInitialized();
        state.Validate();
        CancelAll();
        State = TargetState = state;
        backend.ApplyScreenEffect(state);
    }

    private IOperationHandle StartPersistent(
        string property,
        Func<ScreenEffectState, ScreenEffectState> target,
        Func<ScreenEffectState, ScreenEffectState, double, CancellationToken, Task> animate,
        StageOperationSpec? operation)
    {
        EnsureInitialized();
        var spec = (operation ?? new StageOperationSpec()).Validate();
        IOperationHandle? predecessor;
        ScreenEffectState final;
        lock (gate)
        {
            propertyOperations.TryGetValue(property, out predecessor);
            predecessor?.Complete();
            final = target(TargetState).Validate();
            TargetState = final;
        }
        IOperationHandle? handle = null;
        handle = operations.Start(new OperationDefinition(
            async context =>
            {
                if (predecessor is not null)
                    await predecessor.Completion.WaitAsync(context.CancellationToken);
                ScreenEffectState from;
                lock (gate) from = State;
                await animate(from, final, spec.DurationSeconds, context.CancellationToken);
                lock (gate) State = Merge(State, final, property);
            },
            spec.OperationId,
            spec.GroupId,
            _ =>
            {
                lock (gate) State = Merge(State, final, property);
                backend.ApplyScreenEffect(TargetState);
                return Task.CompletedTask;
            },
            _ =>
            {
                lock (gate) TargetState = Merge(TargetState, State, property);
                backend.ApplyScreenEffect(State);
                return Task.CompletedTask;
            },
            spec.Skippable));
        lock (gate) propertyOperations[property] = handle;
        _ = ClearPropertyWhenDoneAsync(property, handle);
        return handle;
    }

    private static ScreenEffectState Merge(
        ScreenEffectState current,
        ScreenEffectState source,
        string property) => property == "overlay"
        ? current with { OverlayColor = source.OverlayColor, OverlayOpacity = source.OverlayOpacity }
        : current with { LetterboxRatio = source.LetterboxRatio };

    private async Task ClearPropertyWhenDoneAsync(string property, IOperationHandle handle)
    {
        try { await handle.Completion; } catch { }
        lock (gate)
            if (propertyOperations.TryGetValue(property, out var current) && ReferenceEquals(current, handle))
                propertyOperations.Remove(property);
    }
    private async Task ClearFlashWhenDoneAsync(IOperationHandle handle)
    {
        try { await handle.Completion; } catch { }
        lock (gate) if (ReferenceEquals(flashOperation, handle)) flashOperation = null;
    }
    private void CancelAll()
    {
        IOperationHandle[] snapshot;
        lock (gate)
        {
            snapshot = propertyOperations.Values.Append(flashOperation)
                .Where(value => value is not null).Cast<IOperationHandle>().Distinct().ToArray();
            propertyOperations.Clear();
            flashOperation = null;
        }
        foreach (var operation in snapshot) operation.Cancel();
    }
    private void EnsureInitialized()
    {
        if (!isInitialized) throw new InvalidOperationException("ScreenEffectManager is not initialized.");
    }
}
