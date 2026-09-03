#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Diagnostics;
using Majo.Runtime.Dialogue.Commands;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Actors;

namespace Majo.Runtime.Plugins;

public sealed class PluginContext
{
    public PluginContext(
        IServiceResolver services,
        MajoProjectConfiguration? configuration,
        IRuntimeLogger logger)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Configuration = configuration;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IServiceResolver Services { get; }

    public MajoProjectConfiguration? Configuration { get; }

    public IRuntimeLogger Logger { get; }
}

/// Registration-only context. All catalog and factory registrations happen before
/// the service graph is built, so plugins cannot mutate a live runtime accidentally.
public sealed class PluginRegistrationContext
{
    public PluginRegistrationContext(
        RuntimeCommandRegistry commands,
        ITransitionDriverRegistry transitions,
        ICameraModifierRegistry cameraModifiers,
        ITransientCueHandlerRegistry? transientCueHandlers = null)
    {
        Commands = commands;
        Transitions = transitions;
        CameraModifiers = cameraModifiers;
        TransientCueHandlers = transientCueHandlers ?? new TransientCueHandlerRegistry();
    }

    public RuntimeCommandRegistry Commands { get; }
    public ITransitionDriverRegistry Transitions { get; }
    public ICameraModifierRegistry CameraModifiers { get; }
    public ITransientCueHandlerRegistry TransientCueHandlers { get; }
    private readonly List<Action<ActorManager>> actorFactoryConfigurations = new();

    public void ConfigureActorFactories(Action<ActorManager> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        actorFactoryConfigurations.Add(configure);
    }

    internal IReadOnlyList<Action<ActorManager>> ActorFactoryConfigurations =>
        actorFactoryConfigurations;
}

public interface IMajoPlugin
{
    string Id { get; }

    IReadOnlyCollection<string> Dependencies => Array.Empty<string>();

    string Name => Id;

    string Version => "1.0.0";

    void ConfigureServices(RuntimeServiceRegistry services) { }

    void Configure(PluginRegistrationContext context) { }

    void Initialize(PluginContext context) { }

    Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Initialize(context);
        return Task.CompletedTask;
    }

    Task ResetAsync(PluginContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task ShutdownAsync(PluginContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
