namespace Majo.Runtime.Core;

public enum RuntimeHostState
{
    Created,
    Initializing,
    Initialized,
    Resetting,
    ShuttingDown,
    Shutdown,
    Faulted,
}
