#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Direction.Execution;

public sealed class TransientCueExecutionContext
{
    public TransientCueExecutionContext(
        ICameraModifierManager cameraModifiers,
        IServiceResolver? services = null)
    {
        CameraModifiers = cameraModifiers ?? throw new ArgumentNullException(nameof(cameraModifiers));
        Services = services;
    }

    public ICameraModifierManager CameraModifiers { get; }
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
