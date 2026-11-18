#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue;

public class DialogueException : MajoRuntimeException
{
    public DialogueException(string message)
        : base(message)
    {
    }

    public DialogueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DialogueStateException : DialogueException
{
    public DialogueStateException(DialogueSessionState state, string operation)
        : base($"Cannot {operation} dialogue while it is {state}.")
    {
        State = state;
        Operation = operation;
    }

    public DialogueSessionState State { get; }

    public string Operation { get; }
}

public sealed class DialogueVariableException : DialogueException
{
    public DialogueVariableException(string message)
        : base(message)
    {
    }
}

public sealed class DialogueProjectException : DialogueException
{
    public DialogueProjectException(string projectId, string message)
        : base($"Dialogue project '{projectId}' failed: {message}")
    {
        ProjectId = projectId;
    }

    public DialogueProjectException(
        string projectId,
        string message,
        Exception innerException)
        : base($"Dialogue project '{projectId}' failed: {message}", innerException)
    {
        ProjectId = projectId;
    }

    public string ProjectId { get; }
}
