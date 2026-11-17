using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Core;

/// <summary>Runs after every service has initialized; deactivates before any service shuts down.
/// Activation failures also deactivate the participant whose activation was attempted.</summary>
public interface IRuntimeActivation
{
    Task ActivateAsync(IServiceResolver services, CancellationToken cancellationToken);

    Task DeactivateAsync(CancellationToken cancellationToken);
}
