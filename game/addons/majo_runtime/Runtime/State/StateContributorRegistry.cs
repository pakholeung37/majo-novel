#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Majo.Runtime.State;

public sealed class StateContributorRegistry
{
    private readonly Dictionary<string, IStateContributor> contributors =
        new(StringComparer.Ordinal);
    private readonly List<string> registrationOrder = new();
    private bool isBuilt;

    public StateContributorRegistry Register(
        IStateContributor contributor,
        bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        ArgumentException.ThrowIfNullOrWhiteSpace(contributor.Key);
        if (isBuilt)
        {
            throw new StateRuntimeException(
                "State contributors must be registered before the registry is built.");
        }

        if (!replace && contributors.ContainsKey(contributor.Key))
        {
            throw new DuplicateStateContributorException(contributor.Key);
        }

        if (!contributors.ContainsKey(contributor.Key))
        {
            registrationOrder.Add(contributor.Key);
        }

        contributors[contributor.Key] = contributor;
        return this;
    }

    public IReadOnlyList<IStateContributor> Build() => Build(Array.Empty<string>());

    public IReadOnlyList<IStateContributor> Build(IEnumerable<string> externalDependencies)
    {
        ArgumentNullException.ThrowIfNull(externalDependencies);
        if (isBuilt)
        {
            throw new StateRuntimeException("State contributor registry is already built.");
        }

        var ordered = new List<IStateContributor>(contributors.Count);
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var path = new List<string>();
        var external = externalDependencies.ToHashSet(StringComparer.Ordinal);
        foreach (var key in registrationOrder)
        {
            Visit(key, states, path, ordered, external);
        }

        isBuilt = true;
        return new ReadOnlyCollection<IStateContributor>(ordered);
    }

    private void Visit(
        string key,
        IDictionary<string, VisitState> states,
        IList<string> path,
        ICollection<IStateContributor> ordered,
        IReadOnlySet<string> externalDependencies)
    {
        if (!contributors.TryGetValue(key, out var contributor))
        {
            if (externalDependencies.Contains(key))
            {
                return;
            }
            var dependencyPath = string.Join(" -> ", path.Append(key));
            throw new StateRuntimeException(
                $"State contributor dependency '{key}' is not registered. Path: {dependencyPath}.");
        }

        if (states.TryGetValue(key, out var state))
        {
            if (state == VisitState.Visited)
            {
                return;
            }

            var start = path.IndexOf(key);
            var cycle = path.Skip(start).Append(key);
            throw new StateRuntimeException(
                $"State contributor dependency cycle detected: {string.Join(" -> ", cycle)}.");
        }

        states[key] = VisitState.Visiting;
        path.Add(key);
        foreach (var dependency in contributor.Dependencies)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dependency);
            Visit(dependency, states, path, ordered, externalDependencies);
        }

        path.RemoveAt(path.Count - 1);
        states[key] = VisitState.Visited;
        ordered.Add(contributor);
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
