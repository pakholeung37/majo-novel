#nullable enable

using System.Collections.Generic;
using Majo.Runtime.Core;

namespace Majo.Runtime.Operations;

public interface IOperationRunner : IRuntimeService
{
    IReadOnlyCollection<IOperationHandle> ActiveOperations { get; }

    IOperationHandle Start(OperationDefinition definition);
}
