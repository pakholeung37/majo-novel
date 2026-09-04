#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Operations;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Direction.Execution;

public sealed class TransientCueExecutionContext
{
    public TransientCueExecutionContext(
        ICameraModifierManager cameraModifiers,
        IScreenEffectManager screenEffects,
        IStageCurtainManager curtain,
        IServiceResolver? services = null)
    {
        CameraModifiers = cameraModifiers ?? throw new ArgumentNullException(nameof(cameraModifiers));
        ScreenEffects = screenEffects ?? throw new ArgumentNullException(nameof(screenEffects));
        Curtain = curtain ?? throw new ArgumentNullException(nameof(curtain));
        Services = services;
    }

    public ICameraModifierManager CameraModifiers { get; }
    public IScreenEffectManager ScreenEffects { get; }
    public IStageCurtainManager Curtain { get; }
    public IServiceResolver? Services { get; }
}

public interface ITransientCueHandler
{
    string Id { get; }
    bool CanHandle(ITransientDirectionCue cue);
    void Validate(ITransientDirectionCue cue);
    IOperationHandle Execute(ITransientDirectionCue cue, TransientCueExecutionContext context);
}

public interface ITransientCueHandlerRegistry : IRuntimeService
{
    IReadOnlyCollection<ITransientCueHandler> Handlers { get; }
    void Register(ITransientCueHandler handler, bool replace = false);
    bool TryResolve(ITransientDirectionCue cue, out ITransientCueHandler? handler);
    ITransientCueHandler Resolve(ITransientDirectionCue cue);
    void Seal();
}

public sealed class TransientCueHandlerRegistry : ITransientCueHandlerRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, ITransientCueHandler> handlers =
        new(StringComparer.OrdinalIgnoreCase);
    private bool sealedRegistry;

    public TransientCueHandlerRegistry(bool registerDefaults = true)
    {
        if (!registerDefaults) return;
        Register(new CameraFxCueHandler());
        Register(new CurtainCueHandler());
        Register(new GenericCurtainCueHandler());
    }

    public IReadOnlyCollection<ITransientCueHandler> Handlers
    {
        get { lock (gate) return handlers.Values.ToArray(); }
    }

    public void Register(ITransientCueHandler handler, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(handler.Id);
        lock (gate)
        {
            if (sealedRegistry)
                throw new InvalidOperationException("Transient cue handler registry is sealed.");
            if (!replace && handlers.ContainsKey(handler.Id))
                throw new InvalidOperationException(
                    $"Transient cue handler '{handler.Id}' is already registered.");
            handlers[handler.Id] = handler;
        }
    }

    public bool TryResolve(ITransientDirectionCue cue, out ITransientCueHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(cue);
        lock (gate)
        {
            handler = handlers.Values.FirstOrDefault(candidate => candidate.CanHandle(cue));
            return handler is not null;
        }
    }

    public ITransientCueHandler Resolve(ITransientDirectionCue cue)
    {
        if (TryResolve(cue, out var handler) && handler is not null) return handler;
        throw new DirectionException($"No transient cue handler registered for '{cue.GetType().Name}'.");
    }

    public void Seal()
    {
        lock (gate) sealedRegistry = true;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class CameraFxCueHandler : ITransientCueHandler
{
    public string Id => "camera-fx";
    public bool CanHandle(ITransientDirectionCue cue) => cue is CameraFxCue;
    public void Validate(ITransientDirectionCue cue)
    {
        var value = (CameraFxCue)cue;
        if (string.IsNullOrWhiteSpace(value.EffectName))
            throw new DirectionException("Camera FX cue effect name cannot be empty.");
        ValidateDuration(value.DurationSeconds);
    }
    public IOperationHandle Execute(ITransientDirectionCue cue, TransientCueExecutionContext context) =>
        context.CameraModifiers.ApplyEffect((CameraFxCue)cue);

    private static void ValidateDuration(double duration)
    {
        if (!double.IsFinite(duration) || duration < 0)
            throw new DirectionException("Transient cue duration must be finite and non-negative.");
    }
}

internal sealed class CurtainCueHandler : ITransientCueHandler
{
    public string Id => "curtain";
    public bool CanHandle(ITransientDirectionCue cue) => cue is CurtainCue;
    public void Validate(ITransientDirectionCue cue)
    {
        var value = (CurtainCue)cue;
        value.Color.Validate();
        if (!double.IsFinite(value.DurationSeconds) || value.DurationSeconds < 0)
            throw new DirectionException("Transient cue duration must be finite and non-negative.");
    }
    public IOperationHandle Execute(ITransientDirectionCue cue, TransientCueExecutionContext context)
    {
        var value = (CurtainCue)cue;
        return context.Curtain.SetCurtain(
            new Color(value.Color.R, value.Color.G, value.Color.B, value.Color.A),
            value.Open ? 0 : 1,
            new StageOperationSpec(value.DurationSeconds));
    }
}

internal sealed class GenericCurtainCueHandler : ITransientCueHandler
{
    public string Id => "generic-curtain";
    public bool CanHandle(ITransientDirectionCue cue) =>
        cue is DirectionCue value && value.Name.Equals("curtain", StringComparison.OrdinalIgnoreCase);
    public void Validate(ITransientDirectionCue cue)
    {
        var value = (DirectionCue)cue;
        var duration = Read(value, "over", 0.4);
        if (!double.IsFinite(duration) || duration < 0)
            throw new DirectionException("Transient cue duration must be finite and non-negative.");
    }
    public IOperationHandle Execute(ITransientDirectionCue cue, TransientCueExecutionContext context)
    {
        var value = (DirectionCue)cue;
        var open = value.Arguments.TryGetValue("state", out var state) &&
            state.Equals("open", StringComparison.OrdinalIgnoreCase);
        return context.Curtain.SetCurtain(
            Colors.Black,
            open ? 0 : 1,
            new StageOperationSpec(Read(value, "over", 0.4)));
    }

    private static double Read(DirectionCue cue, string name, double fallback) =>
        cue.Arguments.TryGetValue(name, out var value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result : fallback;
}
