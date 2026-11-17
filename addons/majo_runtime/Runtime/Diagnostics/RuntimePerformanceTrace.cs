#nullable enable

using System.Diagnostics;

namespace Majo.Runtime.Diagnostics;

/// <summary>Optional timing spans. No activities are allocated without a listener.
/// Async spans measure latency, not CPU or GPU execution time.</summary>
public static class RuntimePerformanceTrace
{
    public const string SourceName = "Majo.Runtime.Performance";
    public static readonly ActivitySource Source = new(SourceName);
}
