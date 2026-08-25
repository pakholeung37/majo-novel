using Majo.Runtime.Core;

namespace Majo.Runtime.Operations;

public class OperationSchedulerException : MajoRuntimeException
{
    public OperationSchedulerException(string message)
        : base(message)
    {
    }
}

public sealed class DuplicateOperationIdException : OperationSchedulerException
{
    public DuplicateOperationIdException(string id)
        : base($"An active operation with ID '{id}' already exists.")
    {
    }
}

public sealed class OperationNotFoundException : OperationSchedulerException
{
    public OperationNotFoundException(OperationTarget target)
        : base($"No active operation matches '{target}'.")
    {
    }
}
