#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Core;

public sealed record RuntimeLogEvent(
    DateTimeOffset Timestamp,
    RuntimeLogLevel Level,
    string Category,
    string EventId,
    string Message,
    Exception? Exception = null,
    RuntimeLogCorrelation Correlation = default,
    IReadOnlyDictionary<string, object?>? Properties = null)
{
    public IReadOnlyDictionary<string, object?> Properties { get; init; } =
        Properties ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}
