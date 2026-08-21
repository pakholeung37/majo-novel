#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Core;

public sealed class RuntimeServiceRegistry
{
    private readonly Dictionary<Type, RuntimeServiceDescriptor> descriptors = new();
    private readonly List<Type> registrationOrder = new();

    public RuntimeServiceRegistry AddSingleton<TService>(
        Func<IServiceResolver, TService> factory,
        params Type[] dependencies)
        where TService : class, IRuntimeService
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Add(
            typeof(TService),
            resolver => RequireRuntimeService(typeof(TService), factory(resolver)),
            dependencies,
            replace: false);
    }

    public RuntimeServiceRegistry ReplaceSingleton<TService>(
        Func<IServiceResolver, TService> factory,
        params Type[] dependencies)
        where TService : class, IRuntimeService
    {
        ArgumentNullException.ThrowIfNull(factory);
        var serviceType = typeof(TService);
        if (!descriptors.ContainsKey(serviceType))
        {
            throw new ServiceResolutionException(
                $"Cannot replace service '{serviceType.FullName}' because it is not registered.");
        }

        return Add(
            serviceType,
            resolver => RequireRuntimeService(serviceType, factory(resolver)),
            dependencies,
            replace: true);
    }

    public RuntimeHost Build(IRuntimeLogger? logger = null)
    {
        var graph = BuildGraph();
        return new RuntimeHost(graph, logger ?? NullRuntimeLogger.Instance);
    }

    private RuntimeServiceRegistry Add(
        Type serviceType,
        Func<IServiceResolver, IRuntimeService> factory,
        IReadOnlyCollection<Type>? dependencies,
        bool replace)
    {
        if (!replace && descriptors.ContainsKey(serviceType))
        {
            throw new DuplicateServiceRegistrationException(serviceType);
        }

        var normalizedDependencies = (dependencies ?? Array.Empty<Type>())
            .Select(dependency => dependency ?? throw new ArgumentException(
                "Service dependencies cannot contain null.", nameof(dependencies)))
            .Distinct()
            .ToArray();

        descriptors[serviceType] = new RuntimeServiceDescriptor(
            serviceType,
            factory,
            normalizedDependencies);

        if (!replace)
        {
            registrationOrder.Add(serviceType);
        }

        return this;
    }

    private RuntimeServiceGraph BuildGraph()
    {
        var ordered = new List<RuntimeServiceDescriptor>(descriptors.Count);
        var states = new Dictionary<Type, VisitState>();
        var path = new List<Type>();

        foreach (var serviceType in registrationOrder)
        {
            Visit(serviceType, states, path, ordered);
        }

        return new RuntimeServiceGraph(ordered);
    }

    private void Visit(
        Type serviceType,
        IDictionary<Type, VisitState> states,
        IList<Type> path,
        ICollection<RuntimeServiceDescriptor> ordered)
    {
        if (states.TryGetValue(serviceType, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            var cycleStart = path.IndexOf(serviceType);
            var cycle = path.Skip(cycleStart).Append(serviceType).ToArray();
            throw new CircularServiceDependencyException(cycle);
        }

        states[serviceType] = VisitState.Visiting;
        path.Add(serviceType);
        var descriptor = descriptors[serviceType];

        foreach (var dependencyType in descriptor.Dependencies)
        {
            if (dependencyType == typeof(IRuntimeLifetime))
            {
                continue;
            }

            if (!descriptors.ContainsKey(dependencyType))
            {
                throw new MissingServiceDependencyException(path.Append(dependencyType).ToArray());
            }

            Visit(dependencyType, states, path, ordered);
        }

        path.RemoveAt(path.Count - 1);
        states[serviceType] = VisitState.Visited;
        ordered.Add(descriptor);
    }

    private static IRuntimeService RequireRuntimeService(Type serviceType, object? service)
    {
        if (service is null)
        {
            throw new ServiceResolutionException(
                $"Factory for service '{serviceType.FullName}' returned null.");
        }

        if (service is not IRuntimeService runtimeService)
        {
            throw new ServiceResolutionException(
                $"Service '{serviceType.FullName}' must implement {nameof(IRuntimeService)}.");
        }

        return runtimeService;
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}

internal sealed record RuntimeServiceDescriptor(
    Type ServiceType,
    Func<IServiceResolver, IRuntimeService> Factory,
    IReadOnlyList<Type> Dependencies);

internal sealed class RuntimeServiceGraph
{
    public RuntimeServiceGraph(IReadOnlyList<RuntimeServiceDescriptor> orderedDescriptors)
    {
        OrderedDescriptors = orderedDescriptors;
        Descriptors = orderedDescriptors.ToDictionary(descriptor => descriptor.ServiceType);
    }

    public IReadOnlyList<RuntimeServiceDescriptor> OrderedDescriptors { get; }

    public IReadOnlyDictionary<Type, RuntimeServiceDescriptor> Descriptors { get; }
}
