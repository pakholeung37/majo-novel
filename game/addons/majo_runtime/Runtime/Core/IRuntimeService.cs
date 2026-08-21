using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Core;

public interface IRuntimeService
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken);
}
