#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Core;

public sealed class RuntimeHost : IRuntimeLifetime, IAsyncDisposable
{
    private readonly RuntimeServiceGraph graph;
    private readonly IRuntimeLogger logger;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly RuntimeServiceResolver resolver;
    private readonly List<RuntimeServiceDescriptor> initializedServices = new();
    private readonly CancellationTokenSource runtimeCancellation = new();
    private CancellationTokenSource sessionCancellation = new();
    private bool isDisposed;

    internal RuntimeHost(RuntimeServiceGraph graph, IRuntimeLogger logger)
    {
        this.graph = graph;
        this.logger = logger;
        resolver = new RuntimeServiceResolver(graph, this);
    }

    public RuntimeHostState State { get; private set; } = RuntimeHostState.Created;

    public IServiceResolver Services => resolver;

    public CancellationToken RuntimeCancellationToken => runtimeCancellation.Token;

    public CancellationToken SessionCancellationToken => sessionCancellation.Token;

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
            logger.Log(RuntimeLogLevel.Information, "Majo Runtime initialization started.");

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
                    }
                    catch (Exception exception)
                    {
                        throw new RuntimeLifecycleException(
                            $"Service '{descriptor.ServiceType.FullName}' failed to initialize.",
                            exception);
                    }
                }

                State = RuntimeHostState.Initialized;
                logger.Log(RuntimeLogLevel.Information, "Majo Runtime initialization completed.");
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
                logger.Log(RuntimeLogLevel.Error, exception.Message, exception);
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
            logger.Log(RuntimeLogLevel.Information, "Majo Runtime reset started.");

            var previousSessionCancellation = sessionCancellation;
            var exceptions = new List<Exception>();
            TryCancel(previousSessionCancellation, "previous session", exceptions);
            sessionCancellation = new CancellationTokenSource();
            previousSessionCancellation.Dispose();

            foreach (var descriptor in initializedServices)
            {
                try
                {
                    await resolver.GetRuntimeService(descriptor.ServiceType)
                        .ResetAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    exceptions.Add(new RuntimeLifecycleException(
                        $"Service '{descriptor.ServiceType.FullName}' failed to reset.",
                        exception));
                }
            }

            if (exceptions.Count == 0)
            {
                State = RuntimeHostState.Initialized;
                logger.Log(RuntimeLogLevel.Information, "Majo Runtime reset completed.");
                return;
            }

            State = RuntimeHostState.Faulted;
            TryCancel(sessionCancellation, "replacement session", exceptions);
            var wrapped = new RuntimeLifecycleException(
                "One or more Majo Runtime services failed to reset.",
                new AggregateException(exceptions));
            logger.Log(RuntimeLogLevel.Error, wrapped.Message, wrapped);
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
            logger.Log(RuntimeLogLevel.Information, "Majo Runtime shutdown started.");
            var exceptions = new List<Exception>();
            CancelLifetimes(exceptions);

            exceptions.AddRange(await ShutdownInitializedServicesAsync(cancellationToken));
            State = exceptions.Count == 0
                ? RuntimeHostState.Shutdown
                : RuntimeHostState.Faulted;

            ThrowIfShutdownFailed(exceptions);

            logger.Log(RuntimeLogLevel.Information, "Majo Runtime shutdown completed.");
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

    private void CancelLifetimes(ICollection<Exception> exceptions)
    {
        TryCancel(sessionCancellation, "session", exceptions);
        TryCancel(runtimeCancellation, "runtime", exceptions);
    }

    private void ThrowIfShutdownFailed(IReadOnlyCollection<Exception> exceptions)
    {
        if (exceptions.Count == 0)
        {
            return;
        }

        var aggregate = new AggregateException(exceptions);
        var wrapped = new RuntimeLifecycleException(
            "One or more Majo Runtime services failed to shut down.", aggregate);
        logger.Log(RuntimeLogLevel.Error, wrapped.Message, wrapped);
        throw wrapped;
    }

    private static void TryCancel(
        CancellationTokenSource cancellation,
        string lifetimeName,
        ICollection<Exception> exceptions)
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
        IReadOnlyCollection<Exception> cleanupExceptions)
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
        }

        public TService Get<TService>() where TService : class =>
            (TService)Get(typeof(TService));

        public object Get(Type serviceType)
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
            (IRuntimeService)Get(serviceType);

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

            public TService Get<TService>() where TService : class =>
                (TService)Get(typeof(TService));

            public object Get(Type serviceType)
            {
                ArgumentNullException.ThrowIfNull(serviceType);
                if (!requester.Dependencies.Contains(serviceType))
                {
                    throw new ServiceResolutionException(
                        $"Service '{requester.ServiceType.FullName}' requested undeclared " +
                        $"dependency '{serviceType.FullName}'. Dependency chain: " +
                        $"{requester.ServiceType.FullName} -> {serviceType.FullName}.");
                }

                return resolver.Get(serviceType);
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
