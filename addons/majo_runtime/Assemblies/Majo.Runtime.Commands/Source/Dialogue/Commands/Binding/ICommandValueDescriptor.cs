#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record CommandValueDescription(
    string? DisplayName = null,
    string? Syntax = null,
    IReadOnlyList<string>? CanonicalValues = null,
    IReadOnlyDictionary<string, string>? Aliases = null,
    IReadOnlyList<string>? Examples = null)
{
    public CommandValueDescription Snapshot() => this with
    {
        CanonicalValues = CanonicalValues is null ? null : Array.AsReadOnly(System.Linq.Enumerable.ToArray(CanonicalValues)),
        Examples = Examples is null ? null : Array.AsReadOnly(System.Linq.Enumerable.ToArray(Examples)),
        Aliases = Aliases is null ? null : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Aliases, StringComparer.Ordinal)),
    };
}

public interface ICommandValueDescriptor
{
    CommandValueDescription? Description { get; }

    string FormatUntyped(object? value);
}

public interface ICommandValueDescriptor<T> : ICommandValueDescriptor
{
    string Format(T value);

    string ICommandValueDescriptor.FormatUntyped(object? value)
    {
        if (value is T typed)
        {
            return Format(typed);
        }

        throw new ArgumentException($"Expected value of type '{typeof(T).FullName}'.", nameof(value));
    }
}
