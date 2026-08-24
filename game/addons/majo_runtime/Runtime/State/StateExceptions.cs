#nullable enable

using System;
using System.Linq;

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

public sealed class StateEnvelopeException : StateRuntimeException
{
    public StateEnvelopeException(string message) : base(message) { }

    public StateEnvelopeException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class StateIntegrityException : StateRuntimeException
{
    public StateIntegrityException(string message) : base(message) { }
}

public sealed class StateCompatibilityException : StateRuntimeException
{
    public StateCompatibilityException(string message) : base(message) { }

    public StateCompatibilityException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class StateEligibilityException : StateRuntimeException
{
    public StateEligibilityException(StateEligibilityResult result)
        : base($"State operation '{result.Operation}' is not eligible: " +
            string.Join(", ", result.Reasons.Select(value => value.Code)))
    {
        Result = result;
    }

    public StateEligibilityResult Result { get; }
}

public class StateStorageException : StateRuntimeException
{
    public StateStorageException(string message) : base(message) { }

    public StateStorageException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class StateStorageNotFoundException : StateStorageException
{
    public StateStorageNotFoundException(string key)
        : base($"State object '{key}' was not found.") { }
}
