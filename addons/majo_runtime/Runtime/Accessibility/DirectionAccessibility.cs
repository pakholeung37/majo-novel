#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Accessibility;

public sealed class DirectionAccessibility : IDirectionAccessibility
{
    public DirectionAccessibility(bool reduceMotion = false) => ReduceMotion = reduceMotion;

    public bool ReduceMotion { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
