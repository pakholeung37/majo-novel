#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public enum CommandFailurePhase
{
    Schema,
    Binding,
    Execution,
}

public sealed class RuntimeCommandException : Exception
{
    public RuntimeCommandException(
        string commandName,
        string message,
        bool recoverable = true,
        CommandFailurePhase phase = CommandFailurePhase.Binding)
        : base($"Majo command '{commandName}' failed: {message}")
    {
        CommandName = commandName;
        IsRecoverable = recoverable;
        FailurePhase = phase;
    }

    public RuntimeCommandException(
        string commandName,
        string message,
        Exception innerException,
        bool recoverable = true,
        CommandFailurePhase phase = CommandFailurePhase.Binding)
        : base($"Majo command '{commandName}' failed: {message}", innerException)
    {
        CommandName = commandName;
        IsRecoverable = recoverable;
        FailurePhase = phase;
    }

    public string CommandName { get; }

    /// <summary>
    /// Whether the failure represents invalid script/content that can be skipped safely.
    /// Unexpected runtime failures are non-recoverable and still fault the dialogue.
    /// </summary>
    public bool IsRecoverable { get; }

    public CommandFailurePhase FailurePhase { get; }
}
