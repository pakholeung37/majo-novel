#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RuntimeCommandException : Exception
{
    public RuntimeCommandException(string commandName, string message)
        : base($"Majo command '{commandName}' failed: {message}")
    {
        CommandName = commandName;
    }

    public RuntimeCommandException(
        string commandName,
        string message,
        Exception innerException)
        : base($"Majo command '{commandName}' failed: {message}", innerException)
    {
        CommandName = commandName;
    }

    public string CommandName { get; }
}
