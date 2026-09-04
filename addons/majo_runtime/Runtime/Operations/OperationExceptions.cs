#nullable enable

using Majo.Runtime.Core;

namespace Majo.Runtime.Operations;

public class OperationRunnerException : MajoRuntimeException
{
    public OperationRunnerException(string message)
        : base(message)
    {
    }
}
