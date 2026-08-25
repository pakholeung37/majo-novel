#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Diagnostics;

public interface IRuntimeDiagnostics : IRuntimeService
{
    RuntimeDiagnosticsSnapshot CaptureSnapshot();

    IReadOnlyList<RuntimeLogEvent> RecentEvents { get; }

    Task<string> ExportReportAsync(
        string? destinationPath = null,
        CancellationToken cancellationToken = default);
}
