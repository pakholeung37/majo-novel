#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Auto;
using Majo.Runtime.Audio;
using Majo.Runtime.Dialogue;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Core;

public sealed class RuntimeHost : IRuntimeLifetime, IRuntimeSessionController,
    IRuntimeRestoreBoundary, IAsyncDisposable
{
    private readonly RuntimeServiceGraph graph;
    private readonly IRuntimeLogger logger;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly RuntimeServiceResolver resolver;
    private readonly List<RuntimeServiceDescriptor> initializedServices = new();
    private readonly CancellationTokenSource runtimeCancellation = new();
    private CancellationTokenSource sessionCancellation = new();
    private readonly string runtimeId = Guid.NewGuid().ToString("N");
    private string sessionId = Guid.NewGuid().ToString("N");
    private bool isDisposed;
    private bool directionSkipSubscribed;
    private Task directionSkipTask = Task.CompletedTask;

    internal RuntimeHost(RuntimeServiceGraph graph, IRuntimeLogger logger)
    {
        this.graph = graph;
        this.logger = logger;
        resolver = new RuntimeServiceResolver(graph, this);
    }

    public string RuntimeId => runtimeId;

    public string SessionId => sessionId;

    public RuntimeHostState State { get; private set; } = RuntimeHostState.Created;

    public IServiceResolver Services => resolver;

    public IReadOnlyList<string> InitializedServiceNames =>
        initializedServices
            .Select(descriptor => descriptor.ServiceType.Name)
            .ToArray();

    public CancellationToken RuntimeCancellationToken => runtimeCancellation.Token;

    public CancellationToken SessionCancellationToken => sessionCancellation.Token;

    public string BeginSession()
    {
        if (State != RuntimeHostState.Initialized)
        {
            throw new RuntimeLifecycleException(
                $"Cannot begin a game session while Runtime is '{State}'.");
        }

        var previous = sessionCancellation;
        sessionCancellation = new CancellationTokenSource();
        sessionId = Guid.NewGuid().ToString("N");
        var exceptions = new List<Exception>();
        TryCancel(previous, "previous game session", exceptions);
        previous.Dispose();
        if (logger is IRuntimeLogScopeProvider scopeProvider)
        {
            scopeProvider.SetRootCorrelation(RootCorrelation);
        }
        if (exceptions.Count > 0)
        {
            throw new RuntimeLifecycleException(
                "The previous game session could not be cancelled cleanly.",
                new AggregateException(exceptions));
        }

        logger.Info(
            "core.lifecycle",
            "core.lifecycle.session_started",
            "A new game session started.",
            RootCorrelation);
        return sessionId;
    }

    private RuntimeLogCorrelation RootCorrelation => new(runtimeId, sessionId);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (State == RuntimeHostState.Initialized)
            {
                return;
            }

            EnsureState(RuntimeHostState.Created, "initialize");
            State = RuntimeHostState.Initializing;
            logger.Info("core.lifecycle", "core.lifecycle.initialize_started", "Majo Runtime initialization started.", RootCorrelation);

            try
            {
                foreach (var descriptor in graph.OrderedDescriptors)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var service = resolver.Create(descriptor);
                    initializedServices.Add(descriptor);
                    try
                    {
                        await service.InitializeAsync(cancellationToken);
                        logger.Debug("core.lifecycle", "core.lifecycle.service_initialized", $"Service '{descriptor.ServiceType.Name}' initialized.", RootCorrelation);
                    }
                    catch (Exception exception)
                    {
                        throw new RuntimeLifecycleException(
                            $"Service '{descriptor.ServiceType.FullName}' failed to initialize.",
                            exception);
                    }
                }

                State = RuntimeHostState.Initialized;
                SubscribeDirectionSkip();
                logger.Info("core.lifecycle", "core.lifecycle.initialize_completed", "Majo Runtime initialization completed.", RootCorrelation);
            }
            catch (Exception initializationException)
            {
                State = RuntimeHostState.Faulted;
                var cleanupExceptions = new List<Exception>();
                TryCancel(sessionCancellation, "session", cleanupExceptions);
                TryCancel(runtimeCancellation, "runtime", cleanupExceptions);
                cleanupExceptions.AddRange(await ShutdownInitializedServicesAsync(
                    CancellationToken.None));
                var exception = CreateInitializationException(
                    initializationException,
                    cleanupExceptions);
                logger.Error("core.lifecycle", "core.lifecycle.initialize_failed", exception.Message, exception, RootCorrelation);
                throw exception;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            EnsureState(RuntimeHostState.Initialized, "reset");
            State = RuntimeHostState.Resetting;
            logger.Info("core.lifecycle", "core.lifecycle.reset_started", "Majo Runtime reset started.", RootCorrelation);

            var previousSessionCancellation = sessionCancellation;
            var exceptions = new List<Exception>();
            TryCancel(previousSessionCancellation, "previous session", exceptions);
            sessionCancellation = new CancellationTokenSource();
            sessionId = Guid.NewGuid().ToString("N");
            if (logger is IRuntimeLogScopeProvider scopeProvider)
            {
                scopeProvider.SetRootCorrelation(RootCorrelation);
            }
            previousSessionCancellation.Dispose();

            var directionReset = false;
            var directionStageBackendReset = false;
            var directionRenderHostReset = false;
            if (resolver.TryGet<IDirectionBeatRuntime>(out var beats) && beats is not null)
                beats.AbortOpen();
            if (resolver.TryGet<IDirectionTransactionCoordinator>(out var direction) &&
                direction is not null)
            {
                // This service is handled explicitly below. Mark it handled
                // before invoking it so a failed attempt is not retried later,
                // after its Godot dependencies may already have been reset.
                directionReset = true;
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
                directionStageBackendReset = true;
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
                directionRenderHostReset = true;
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

            foreach (var descriptor in initializedServices)
            {
                if ((directionReset &&
                     descriptor.ServiceType == typeof(IDirectionTransactionCoordinator)) ||
                    (directionStageBackendReset &&
                     descriptor.ServiceType == typeof(GodotDirectionStageBackend)) ||
                    (directionRenderHostReset &&
                     descriptor.ServiceType == typeof(GodotDirectionRenderHost)) ||
                    descriptor.ServiceType == typeof(IRuntimeActivityRegistry))
                    continue;
                try
                {
                    await resolver.GetRuntimeService(descriptor.ServiceType)
                        .ResetAsync(cancellationToken);
                    logger.Debug("core.lifecycle", "core.lifecycle.service_reset", $"Service '{descriptor.ServiceType.Name}' reset.", RootCorrelation);
                }
                catch (Exception exception)
                {
                    exceptions.Add(new RuntimeLifecycleException(
                        $"Service '{descriptor.ServiceType.FullName}' failed to reset.",
                        exception));
                }
            }

            await AddQuiescenceFailureAsync(exceptions, "reset", cancellationToken);

            if (exceptions.Count == 0)
            {
                State = RuntimeHostState.Initialized;
                logger.Info("core.lifecycle", "core.lifecycle.reset_completed", "Majo Runtime reset completed.", RootCorrelation);
                return;
            }

            State = RuntimeHostState.Faulted;
            TryCancel(sessionCancellation, "replacement session", exceptions);
            var wrapped = new RuntimeLifecycleException(
                "One or more Majo Runtime services failed to reset.",
                new AggregateException(exceptions));
            logger.Error("core.lifecycle", "core.lifecycle.reset_failed", wrapped.Message, wrapped, RootCorrelation);
            throw wrapped;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (State == RuntimeHostState.Shutdown)
            {
                return;
            }

            if (State == RuntimeHostState.Created)
            {
                var cancellationExceptions = new List<Exception>();
                CancelLifetimes(cancellationExceptions);
                State = RuntimeHostState.Shutdown;
                ThrowIfShutdownFailed(cancellationExceptions);
                return;
            }

            if (State is not RuntimeHostState.Initialized and not RuntimeHostState.Faulted)
            {
                throw new RuntimeLifecycleException(
                    $"Cannot shutdown Majo Runtime while it is {State}.");
            }

            State = RuntimeHostState.ShuttingDown;
            logger.Info("core.lifecycle", "core.lifecycle.shutdown_started", "Majo Runtime shutdown started.", RootCorrelation);
            var exceptions = new List<Exception>();
            UnsubscribeDirectionSkip();
            CancelLifetimes(exceptions);
            if (!directionSkipTask.IsCompleted)
            {
                try { await directionSkipTask; }
                catch (Exception exception) { exceptions.Add(exception); }
            }

            exceptions.AddRange(await ShutdownInitializedServicesAsync(cancellationToken));
            await AddQuiescenceFailureAsync(exceptions, "shutdown", cancellationToken);
            State = exceptions.Count == 0
                ? RuntimeHostState.Shutdown
                : RuntimeHostState.Faulted;

            ThrowIfShutdownFailed(exceptions);

            logger.Info("core.lifecycle", "core.lifecycle.shutdown_completed", "Majo Runtime shutdown completed.", RootCorrelation);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        try
        {
            await ShutdownAsync();
        }
        finally
        {
            isDisposed = true;
            runtimeCancellation.Dispose();
            sessionCancellation.Dispose();
            lifecycleGate.Dispose();
        }
    }

    private async Task<IReadOnlyList<Exception>> ShutdownInitializedServicesAsync(
        CancellationToken cancellationToken)
    {
        var exceptions = new List<Exception>();

        var snapshot = initializedServices.ToArray();
        for (var index = snapshot.Length - 1; index >= 0; index--)
        {
            var descriptor = snapshot[index];
            try
            {
                await resolver.GetRuntimeService(descriptor.ServiceType)
                    .ShutdownAsync(cancellationToken);
                initializedServices.Remove(descriptor);
            }
            catch (Exception exception)
            {
                exceptions.Add(new RuntimeLifecycleException(
                    $"Service '{descriptor.ServiceType.FullName}' failed to shut down.",
                    exception));
            }
        }
        return exceptions;
    }

    private void CancelLifetimes(List<Exception> exceptions)
    {
        TryCancel(sessionCancellation, "session", exceptions);
        TryCancel(runtimeCancellation, "runtime", exceptions);
    }

    public async Task CancelForRestoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<Exception>();

        if (resolver.TryGet<IDialoguePlayer>(out var dialogue) && dialogue is not null)
        {
            try { await dialogue.StopAsync(cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (resolver.TryGet<IDirectionTransactionCoordinator>(out var direction) && direction is not null)
        {
            try { await direction.ResetAsync(cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (resolver.TryGet<IAudioManager>(out var audio) && audio is not null)
        {
            try { await audio.ResetAsync(cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (resolver.TryGet<IOperationRunner>(out var operations) && operations is not null)
        {
            try { await operations.ResetAsync(cancellationToken); }
            catch (Exception exception) { failures.Add(exception); }
        }

        await AddQuiescenceFailureAsync(failures, "restore", cancellationToken);
        if (failures.Count > 0)
        {
            throw new RuntimeLifecycleException(
                "One or more runtime domains failed to quiesce for restore.",
                new AggregateException(failures));
        }
    }

    public async Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        await AddQuiescenceFailureAsync(failures, "runtime", cancellationToken);
        if (failures.Count > 0)
            throw new AggregateException("Runtime activity did not quiesce.", failures);
    }

    private async Task AddQuiescenceFailureAsync(
        List<Exception> failures,
        string phase,
        CancellationToken cancellationToken)
    {
        if (!resolver.TryGet<IRuntimeActivityObserver>(out var activities) || activities is null)
            return;
        try
        {
            await activities.WaitForQuiescenceAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            var snapshot = string.Join(
                ", ",
                activities.Snapshot().Select(activity =>
                    $"{activity.Owner}/{activity.Type}/{activity.DiagnosticId} " +
                    $"source={activity.Source ?? "<none>"}"));
            failures.Add(new RuntimeLifecycleException(
                $"Timed out or failed waiting for activity quiescence during {phase}. " +
                $"Active: {snapshot}",
                exception));
        }
    }

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
            SessionCancellationToken);
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
                exception,
                RootCorrelation);
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
                exception,
                RootCorrelation);
        }
        await FinishDirectionSkipAsync(direction, cancellationToken);
    }

    private void ThrowIfShutdownFailed(List<Exception> exceptions)
    {
        if (exceptions.Count == 0)
        {
            return;
        }

        var aggregate = new AggregateException(exceptions);
        var wrapped = new RuntimeLifecycleException(
            "One or more Majo Runtime services failed to shut down.", aggregate);
        logger.Error("core.lifecycle", "core.lifecycle.shutdown_failed", wrapped.Message, wrapped, RootCorrelation);
        throw wrapped;
    }

    private static void TryCancel(
        CancellationTokenSource cancellation,
        string lifetimeName,
        List<Exception> exceptions)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            exceptions.Add(new RuntimeLifecycleException(
                $"The {lifetimeName} cancellation callbacks failed.", exception));
        }
    }

    private void EnsureState(RuntimeHostState expected, string operation)
    {
        if (State != expected)
        {
            throw new RuntimeLifecycleException(
                $"Cannot {operation} Majo Runtime while it is {State}; expected {expected}.");
        }
    }

    private static RuntimeLifecycleException CreateInitializationException(
        Exception initializationException,
        List<Exception> cleanupExceptions)
    {
        if (cleanupExceptions.Count == 0)
        {
            return new RuntimeLifecycleException(
                "Majo Runtime initialization failed.", initializationException);
        }

        return new RuntimeLifecycleException(
            "Majo Runtime initialization failed and cleanup reported additional errors.",
            new AggregateException(new[] { initializationException }.Concat(cleanupExceptions)));
    }

    private sealed class RuntimeServiceResolver : IServiceResolver
    {
        private readonly RuntimeServiceGraph graph;
        private readonly Dictionary<Type, object> instances = new();

        public RuntimeServiceResolver(RuntimeServiceGraph graph, IRuntimeLifetime lifetime)
        {
            this.graph = graph;
            instances.Add(typeof(IRuntimeLifetime), lifetime);
            if (lifetime is IRuntimeSessionController sessionController)
            {
                instances.Add(typeof(IRuntimeSessionController), sessionController);
            }
            if (lifetime is IRuntimeRestoreBoundary restoreBoundary)
            {
                instances.Add(typeof(IRuntimeRestoreBoundary), restoreBoundary);
            }
        }

        public TService Resolve<TService>() where TService : class =>
            (TService)Resolve(typeof(TService));

        public object Resolve(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            if (instances.TryGetValue(serviceType, out var service))
            {
                return service;
            }

            if (!graph.Descriptors.ContainsKey(serviceType))
            {
                throw new ServiceResolutionException(
                    $"Service '{serviceType.FullName}' is not registered.");
            }

            throw new ServiceResolutionException(
                $"Service '{serviceType.FullName}' has not been created yet. " +
                "Check its declared dependency chain.");
        }

        public bool TryGet<TService>(out TService? service) where TService : class
        {
            if (instances.TryGetValue(typeof(TService), out var instance))
            {
                service = (TService)instance;
                return true;
            }

            service = null;
            return false;
        }

        public IRuntimeService Create(RuntimeServiceDescriptor descriptor)
        {
            try
            {
                var service = descriptor.Factory(new FactoryResolver(this, descriptor));
                instances.Add(descriptor.ServiceType, service);
                return service;
            }
            catch (MajoRuntimeException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ServiceResolutionException(
                    $"Factory for service '{descriptor.ServiceType.FullName}' failed.",
                    exception);
            }
        }

        public IRuntimeService GetRuntimeService(Type serviceType) =>
            (IRuntimeService)Resolve(serviceType);

        private sealed class FactoryResolver : IServiceResolver
        {
            private readonly RuntimeServiceResolver resolver;
            private readonly RuntimeServiceDescriptor requester;

            public FactoryResolver(
                RuntimeServiceResolver resolver,
                RuntimeServiceDescriptor requester)
            {
                this.resolver = resolver;
                this.requester = requester;
            }

            public TService Resolve<TService>() where TService : class =>
                (TService)Resolve(typeof(TService));

            public object Resolve(Type serviceType)
            {
                ArgumentNullException.ThrowIfNull(serviceType);
                if (!requester.Dependencies.Contains(serviceType))
                {
                    throw new ServiceResolutionException(
                        $"Service '{requester.ServiceType.FullName}' requested undeclared " +
                        $"dependency '{serviceType.FullName}'. Dependency chain: " +
                        $"{requester.ServiceType.FullName} -> {serviceType.FullName}.");
                }

                return resolver.Resolve(serviceType);
            }

            public bool TryGet<TService>(out TService? service) where TService : class
            {
                if (!requester.Dependencies.Contains(typeof(TService)))
                {
                    throw new ServiceResolutionException(
                        $"Service '{requester.ServiceType.FullName}' requested undeclared " +
                        $"dependency '{typeof(TService).FullName}'. Dependency chain: " +
                        $"{requester.ServiceType.FullName} -> {typeof(TService).FullName}.");
                }

                return resolver.TryGet(out service);
            }
        }
    }
}
