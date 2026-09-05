#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CommandParameterGroupRegistry
{
    private readonly Dictionary<string, ICommandParameterGroup> groups =
        new(StringComparer.OrdinalIgnoreCase);
    private bool isSealed;

    public IReadOnlyDictionary<string, ICommandParameterGroup> Groups =>
        new ReadOnlyDictionary<string, ICommandParameterGroup>(groups);

    public CommandParameterGroupRegistry Add(string id, ICommandParameterGroup group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(group);

        if (isSealed)
        {
            throw new InvalidOperationException(
                "Parameter groups cannot be registered after registration is sealed.");
        }

        if (!groups.TryAdd(id, group))
        {
            throw new ArgumentException(
                $"Parameter group '{id}' is already registered.",
                nameof(id));
        }

        return this;
    }

    public CommandParameterGroupRegistry Register(string id, ICommandParameterGroup group) =>
        Add(id, group);

    public bool TryGet(string id, out ICommandParameterGroup? group) =>
        groups.TryGetValue(id, out group);

    public ICommandParameterGroup Get(string id)
    {
        if (!groups.TryGetValue(id, out var group))
        {
            throw new KeyNotFoundException($"Parameter group '{id}' was not found.");
        }
        return group;
    }

    public ICommandParameterGroup<TGroup> Get<TGroup>(string id)
    {
        var group = Get(id);
        if (group is ICommandParameterGroup<TGroup> typed)
        {
            return typed;
        }
        throw new InvalidOperationException(
            $"Parameter group '{id}' is of type '{group.GetType().FullName}', not '{typeof(ICommandParameterGroup<TGroup>).FullName}'.");
    }

    public void Seal()
    {
        isSealed = true;
    }
}
