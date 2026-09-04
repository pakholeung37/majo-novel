using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Core;

public interface IRuntimeLifetime
{
    CancellationToken RuntimeCancellationToken { get; }

    CancellationToken SessionCancellationToken { get; }
}

public interface IRuntimeSessionController
{
    string SessionId { get; }

    string BeginSession();
}

public interface IRuntimeRestoreBoundary
{
    Task CancelForRestoreAsync(CancellationToken cancellationToken = default);

    Task WaitForQuiescenceAsync(CancellationToken cancellationToken = default);
}
