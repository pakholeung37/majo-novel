#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Actors.Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Actors;

[Flags]
public enum ActorFactoryDomains
{
    None = 0,
    Runtime = 1,
    Direction = 2,
}

public delegate ActorDefinition ActorDefinitionCompiler(
    ActorDefinitionConfiguration configuration,
    ActorResourceSettings resources);

public sealed class ActorFactoryRegistry
{
    private readonly Dictionary<string, ActorFactoryRegistration> registrations =
        new(StringComparer.Ordinal);
    private bool isSealed;

    public bool IsSealed => isSealed;

    public IReadOnlyCollection<ActorFactoryRegistration> Registrations =>
        registrations.Values.ToArray();

    public ActorFactoryRegistry Register(
        string factoryId,
        ActorFactoryDomains domains,
        Func<ActorFactoryContext, IActorFactory> factory,
        ActorDefinitionCompiler? compileDefinition = null,
        params Type[] serviceDependencies)
    {
        EnsureOpen();
        var registration = CreateRegistration(
            factoryId, domains, factory, compileDefinition, serviceDependencies);
        if (!registrations.TryAdd(registration.FactoryId, registration))
            throw new DuplicateActorFactoryException(registration.FactoryId);
        return this;
    }

    public ActorFactoryRegistry Replace(
        string factoryId,
        ActorFactoryDomains domains,
        Func<ActorFactoryContext, IActorFactory> factory,
        ActorDefinitionCompiler? compileDefinition = null,
        params Type[] serviceDependencies)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryId);
        if (!registrations.ContainsKey(factoryId))
            throw new ActorFactoryNotFoundException(factoryId, "<registry>");
        registrations[factoryId] = CreateRegistration(
            factoryId, domains, factory, compileDefinition, serviceDependencies);
        return this;
    }

    public ActorFactoryRegistry Seal()
    {
        isSealed = true;
        return this;
    }

    public bool Contains(string factoryId) => registrations.ContainsKey(factoryId);

    public ActorFactoryRegistration GetRequired(string factoryId) =>
        registrations.TryGetValue(factoryId, out var registration)
            ? registration
            : throw new ActorFactoryNotFoundException(factoryId, "<configuration>");

    internal IReadOnlyList<ActorFactoryRegistration> ForDomain(ActorFactoryDomains domain)
    {
        if (!isSealed)
            throw new ActorException("Actor factory registry must be sealed before factories are built.");
        return registrations.Values.Where(entry => (entry.Domains & domain) != 0).ToArray();
    }

    private static ActorFactoryRegistration CreateRegistration(
        string factoryId,
        ActorFactoryDomains domains,
        Func<ActorFactoryContext, IActorFactory> factory,
        ActorDefinitionCompiler? compileDefinition,
        IEnumerable<Type>? dependencies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryId);
        ArgumentNullException.ThrowIfNull(factory);
        if (domains == ActorFactoryDomains.None)
            throw new ArgumentOutOfRangeException(nameof(domains), "At least one actor factory domain is required.");
        var normalizedDependencies = (dependencies ?? Array.Empty<Type>())
            .Select(type => type ?? throw new ArgumentException("Service dependencies cannot contain null."))
            .Distinct()
            .ToArray();
        var forbiddenDependency = normalizedDependencies.FirstOrDefault(type =>
            type == typeof(IActorManager) ||
            type == typeof(IDirectionTransactionCoordinator) ||
            type == typeof(IDirectionPreparationBackend));
        if (forbiddenDependency is not null)
            throw new ActorException(
                $"Actor factory '{factoryId}' cannot depend on '{forbiddenDependency.Name}'.");
        return new ActorFactoryRegistration(
            factoryId.Trim(), domains, factory, compileDefinition, normalizedDependencies);
    }

    private void EnsureOpen()
    {
        if (isSealed)
            throw new ActorException("Actor factory registry is sealed.");
    }
}

public sealed record ActorFactoryRegistration(
    string FactoryId,
    ActorFactoryDomains Domains,
    Func<ActorFactoryContext, IActorFactory> BuildFactory,
    ActorDefinitionCompiler? CompileDefinition,
    IReadOnlyList<Type> ServiceDependencies);
