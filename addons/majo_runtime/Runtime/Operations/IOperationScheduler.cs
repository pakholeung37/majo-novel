using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Core;

namespace Majo.Runtime.Operations;

public interface IOperationScheduler : IRuntimeService
{
    IReadOnlyCollection<IOperationHandle> ActiveOperations { get; }

    IOperationHandle Start(OperationDefinition definition);

    Task WaitAsync(OperationTarget target, CancellationToken cancellationToken = default);

    Task CancelAsync(OperationTarget target, CancellationToken cancellationToken = default);

    Task CompleteAsync(OperationTarget target, CancellationToken cancellationToken = default);
}
