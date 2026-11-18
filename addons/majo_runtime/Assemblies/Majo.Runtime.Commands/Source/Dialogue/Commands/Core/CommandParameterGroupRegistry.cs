#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Majo.Runtime.Dialogue.Commands;

public interface ICommandParameterGroupRegistrar
{
    ICommandParameterGroupRegistrar Add(string id, CommandGroupDeclaration group);

    ICommandParameterGroupRegistrar Register(string id, CommandGroupDeclaration group) =>
        Add(id, group);

}

public sealed class CommandParameterGroupRegistry : ICommandParameterGroupRegistrar
{
    private readonly Dictionary<string, CommandGroupDeclaration> groups =
        new(StringComparer.OrdinalIgnoreCase);
    private bool isSealed;

    public IReadOnlyDictionary<string, CommandGroupDeclaration> Groups =>
        new ReadOnlyDictionary<string, CommandGroupDeclaration>(groups);

    public CommandParameterGroupRegistry Add(string id, CommandGroupDeclaration group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(group);

        if (isSealed)
        {
            throw new InvalidOperationException(
                "Parameter groups cannot be registered after registration is sealed.");
        }

        if (!groups.TryAdd(id, CommandContractValidation.SnapshotGroup(group)))
        {
            throw new ArgumentException(
                $"Parameter group '{id}' is already registered.",
                nameof(id));
        }

        return this;
    }

    ICommandParameterGroupRegistrar ICommandParameterGroupRegistrar.Add(string id, CommandGroupDeclaration group) =>
        Add(id, group);

    public CommandParameterGroupRegistry Register(string id, CommandGroupDeclaration group) =>
        Add(id, group);

    ICommandParameterGroupRegistrar ICommandParameterGroupRegistrar.Register(string id, CommandGroupDeclaration group) =>
        Add(id, group);


    public bool TryGet(string id, out CommandGroupDeclaration? group) =>
        groups.TryGetValue(id, out group);

    public CommandGroupDeclaration Get(string id)
    {
        if (!groups.TryGetValue(id, out var group))
        {
            throw new KeyNotFoundException($"Parameter group '{id}' was not found.");
        }
        return group;
    }

    public void Seal()
    {
        isSealed = true;
    }

    internal CommandParameterGroupSnapshot CreateSnapshot() =>
        new(new Dictionary<string, CommandGroupDeclaration>(groups, StringComparer.OrdinalIgnoreCase));

    internal void RestoreSnapshot(CommandParameterGroupSnapshot snapshot)
    {
        groups.Clear();
        foreach (var (k, v) in snapshot.Groups)
        {
            groups[k] = v;
        }
    }

    internal sealed record CommandParameterGroupSnapshot(
        Dictionary<string, CommandGroupDeclaration> Groups);
}
