using System.Threading;

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
