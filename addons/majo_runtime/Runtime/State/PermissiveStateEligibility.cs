#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.State;

internal sealed class PermissiveStateEligibility : IStateEligibility
{
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public StateEligibilityResult Check(StateOperationKind operation) => new(operation, []);
    public IStateBarrierHandle AcquireBarrier(
        object owner,
        string reasonCode,
        StateOperationMask operations = StateOperationMask.All,
        string? diagnostic = null,
        string? playerMessage = null) => throw new NotSupportedException();
}
