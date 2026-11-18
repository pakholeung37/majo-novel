#nullable enable

using System;
using System.Threading;

namespace Majo.Runtime.Dialogue.Commands;

public interface ICommandInvocationCapabilities
{
}

public sealed class EmptyCommandInvocationCapabilities : ICommandInvocationCapabilities
{
    public static EmptyCommandInvocationCapabilities Instance { get; } = new();

    private EmptyCommandInvocationCapabilities() { }
}

public sealed class CommandExecutionContext
{
    public CommandExecutionContext(
        string executionId,
        string? sourceLineId = null,
        string? dialogueSessionId = null,
        ICommandInvocationCapabilities? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ExecutionId = executionId;
        SourceLineId = sourceLineId;
        DialogueSessionId = dialogueSessionId;
        Capabilities = capabilities ?? EmptyCommandInvocationCapabilities.Instance;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    public string ExecutionId { get; }

    public string? SourceLineId { get; }

    public string? DialogueSessionId { get; }

    public ICommandInvocationCapabilities Capabilities { get; }
}
