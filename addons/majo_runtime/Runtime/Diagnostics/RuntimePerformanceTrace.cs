#nullable enable

using System.Diagnostics;

namespace Majo.Runtime.Diagnostics;

/// <summary>Optional timing spans. No activities are allocated without a listener.
/// Async spans measure latency, not CPU or GPU execution time.</summary>
internal static class RuntimePerformanceTrace
{
    internal const string SourceName = "Majo.Runtime.Performance";
    internal static readonly ActivitySource Source = new(SourceName);
}
