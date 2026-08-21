namespace Majo.Runtime.Operations;

public enum OperationState
{
    Created,
    Running,
    Completing,
    Cancelling,
    Completed,
    Cancelled,
    Faulted,
}
