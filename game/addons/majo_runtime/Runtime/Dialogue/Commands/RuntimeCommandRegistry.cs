#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record RuntimeCommandRegistration(
    string Name,
    Func<IReadOnlyList<string>, RuntimeCommandContext, Task> Handler);

public sealed class RuntimeCommandRegistry
{
    private static readonly HashSet<string> ReservedNames = new(
        new[] { "back", "char", "hide", "await", "cancel", "wait", "stop" },
        StringComparer.Ordinal);

    private readonly Dictionary<string, RuntimeCommandRegistration> registrations =
        new(StringComparer.Ordinal);
    private bool isSealed;

    public IReadOnlyCollection<RuntimeCommandRegistration> Registrations =>
        new ReadOnlyCollection<RuntimeCommandRegistration>(
            new List<RuntimeCommandRegistration>(registrations.Values));

    public RuntimeCommandRegistry Register(
        string name,
        Func<IReadOnlyList<string>, RuntimeCommandContext, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (isSealed)
        {
            throw new RuntimeCommandException(
                name, "Commands must be registered before Runtime initialization.");
        }

        if (name.Any(char.IsWhiteSpace))
        {
            throw new RuntimeCommandException(name, "Command names cannot contain whitespace.");
        }

        if (ReservedNames.Contains(name))
        {
            throw new RuntimeCommandException(
                name, "Command name is reserved by Majo Runtime or Yarn Spinner.");
        }

        if (!registrations.TryAdd(name, new RuntimeCommandRegistration(name, handler)))
        {
            throw new RuntimeCommandException(name, "Command is already registered.");
        }

        return this;
    }

    internal void Seal() => isSealed = true;
}
