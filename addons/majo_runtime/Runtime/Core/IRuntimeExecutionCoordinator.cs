using System;
using System.Collections.Generic;

namespace Majo.Runtime.Core;

/// <summary>Coordinates domain work without coupling the host to a rendering or script backend.</summary>
public interface IRuntimeExecutionCoordinator : IRuntimeService, IRuntimeRestoreBoundary,
    IRuntimeRestoreOutput, IRuntimeHotReloadBoundary
{
    /// <summary>Services reset by this coordinator before the ordinary service reset pass.</summary>
    IReadOnlyCollection<Type> ManagedResetServices { get; }
}
