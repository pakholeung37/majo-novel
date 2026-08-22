#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Majo.Runtime.Resources;

public sealed class ResourceProviderRegistry
{
    private readonly Dictionary<string, Registration> registrations =
        new(StringComparer.Ordinal);
    private int registrationOrder;

    public IReadOnlyList<IResourceProvider> Providers =>
        new ReadOnlyCollection<IResourceProvider>(registrations.Values
            .OrderByDescending(registration => registration.Priority)
            .ThenBy(registration => registration.Order)
            .Select(registration => registration.Provider)
            .ToArray());

    public ResourceProviderRegistry Register(
        IResourceProvider provider,
        int priority = 0,
        bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
        if (!replace && registrations.ContainsKey(provider.Id))
        {
            throw new DuplicateResourceProviderException(provider.Id);
        }

        var order = registrations.TryGetValue(provider.Id, out var previous)
            ? previous.Order
            : registrationOrder++;
        registrations[provider.Id] = new Registration(provider, priority, order);
        return this;
    }

    private sealed record Registration(
        IResourceProvider Provider,
        int Priority,
        int Order);
}
