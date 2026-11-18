#nullable enable

using System.Threading;

namespace Majo.Runtime.Operations;

public sealed class OperationContext
{
    internal OperationContext(
        IFrameClock frameClock,
        CancellationToken cancellationToken)
    {
        FrameClock = frameClock;
        CancellationToken = cancellationToken;
    }

    public IFrameClock FrameClock { get; }

    public CancellationToken CancellationToken { get; }
}
