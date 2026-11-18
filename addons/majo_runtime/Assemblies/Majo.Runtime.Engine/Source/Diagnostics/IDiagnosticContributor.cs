#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Diagnostics;

public sealed record DiagnosticSection(
    string Title,
    IReadOnlyDictionary<string, object?> Entries)
{
    public static DiagnosticSection Create(string title, IEnumerable<KeyValuePair<string, object?>> entries) =>
        new(title, new Dictionary<string, object?>(entries, StringComparer.Ordinal));
}

public interface IDiagnosticContributor
{
    string Key { get; }

    DiagnosticSection Capture();
}

public sealed class DiagnosticContributorRegistry
{
    private readonly List<IDiagnosticContributor> contributors = new();

    public IReadOnlyList<IDiagnosticContributor> Contributors => contributors;

    public DiagnosticContributorRegistry Register(IDiagnosticContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        contributors.Add(contributor);
        return this;
    }
}
