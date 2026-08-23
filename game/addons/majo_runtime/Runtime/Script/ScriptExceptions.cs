#nullable enable

using System;

namespace Majo.Runtime.Script;

public class ScriptRuntimeException : Exception
{
    public ScriptRuntimeException(string message)
        : base(message)
    {
    }

    public ScriptRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ScriptCheckpointException : ScriptRuntimeException
{
    public ScriptCheckpointException(string message)
        : base(message)
    {
    }

    public ScriptCheckpointException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
