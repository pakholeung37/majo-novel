#nullable enable

using System;

namespace Majo.Runtime.State;

public class StateRuntimeException : Exception
{
    public StateRuntimeException(string message)
        : base(message)
    {
    }

    public StateRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class StateSnapshotException : StateRuntimeException
{
    public StateSnapshotException(string message)
        : base(message)
    {
    }

    public StateSnapshotException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DuplicateStateContributorException : StateRuntimeException
{
    public DuplicateStateContributorException(string key)
        : base($"State contributor '{key}' is already registered.")
    {
    }
}
