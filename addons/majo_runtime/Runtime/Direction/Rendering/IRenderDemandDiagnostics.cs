using System;
using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.Direction.Rendering;

public sealed record ViewDemandSnapshot(string View, bool Rendering, bool Screen,
    IReadOnlyList<string> Requests, IReadOnlyList<string> Sources);

/// <summary>Godot-thread diagnostics. Disposing the returned scope releases all acquired View demand.</summary>
public interface IRenderDemandDiagnostics : IRuntimeService
{
    IReadOnlyList<ViewDemandSnapshot> InspectViewDemand();
    IDisposable AcquireAllViews(string reason);

    /// <summary>Requests legacy Stage raster outputs, including Stages created during the lease.
    /// Views normally draw World2D directly and do not need these textures.</summary>
    IDisposable AcquireAllStageRasters(string reason) => throw new NotSupportedException();
}
