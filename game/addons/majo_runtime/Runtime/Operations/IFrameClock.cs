using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Operations;

public enum FrameTimeMode
{
    Scaled,
    Unscaled,
}

public interface IFrameClock : IRuntimeService
{
    Task NextFrameAsync(CancellationToken cancellationToken = default);

    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default);

    Task DelayAsync(
        TimeSpan duration,
        FrameTimeMode timeMode,
        CancellationToken cancellationToken = default) =>
        DelayAsync(duration, cancellationToken);
}
