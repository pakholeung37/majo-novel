#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Core;

public interface IRuntimeRestoreOutput
{
    /// <summary>Freeze visible output before stopping any old execution.</summary>
    IRetainedRestoreOutput? RetainRestoreOutput();
}

public interface IRuntimeHotReloadBoundary
{
    Task CancelForHotReloadAsync(CancellationToken cancellationToken);
}

public interface IRetainedRestoreOutput : IDisposable
{
    /// <summary>Wait for the committed output to be rendered under the retained layer.</summary>
    Task WaitForTargetAsync(CancellationToken cancellationToken);
}
