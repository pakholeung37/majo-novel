#nullable enable

using Majo.Runtime.Core;

namespace Majo.Runtime.Operations;

public interface IOperationRunner : IRuntimeService
{
    IOperationHandle Start(OperationDefinition definition);
}
