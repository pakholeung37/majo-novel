using System;
using System.Collections.Generic;
using System.Linq;

namespace Majo.Runtime.Core;

public class MajoRuntimeException : Exception
{
    public MajoRuntimeException(string message)
        : base(message)
    {
    }

    public MajoRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DuplicateServiceRegistrationException : MajoRuntimeException
{
    public DuplicateServiceRegistrationException(Type serviceType)
        : base($"Service '{serviceType.FullName}' is already registered.")
    {
    }
}

public sealed class MissingServiceDependencyException : MajoRuntimeException
{
    public MissingServiceDependencyException(IReadOnlyList<Type> dependencyChain)
        : base($"Service dependency is missing: {FormatChain(dependencyChain)}.")
    {
        DependencyChain = dependencyChain;
    }

    public IReadOnlyList<Type> DependencyChain { get; }

    private static string FormatChain(IEnumerable<Type> chain) =>
        string.Join(" -> ", chain.Select(type => type.FullName));
}

public sealed class CircularServiceDependencyException : MajoRuntimeException
{
    public CircularServiceDependencyException(IReadOnlyList<Type> dependencyChain)
        : base($"Circular service dependency detected: {FormatChain(dependencyChain)}.")
    {
        DependencyChain = dependencyChain;
    }

    public IReadOnlyList<Type> DependencyChain { get; }

    private static string FormatChain(IEnumerable<Type> chain) =>
        string.Join(" -> ", chain.Select(type => type.FullName));
}

public sealed class ServiceResolutionException : MajoRuntimeException
{
    public ServiceResolutionException(string message)
        : base(message)
    {
    }

    public ServiceResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class RuntimeLifecycleException : MajoRuntimeException
{
    public RuntimeLifecycleException(string message)
        : base(message)
    {
    }

    public RuntimeLifecycleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
