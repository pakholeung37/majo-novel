using System.Threading;

namespace Majo.Runtime.Core;

public interface IRuntimeLifetime
{
    CancellationToken RuntimeCancellationToken { get; }

    CancellationToken SessionCancellationToken { get; }
}
