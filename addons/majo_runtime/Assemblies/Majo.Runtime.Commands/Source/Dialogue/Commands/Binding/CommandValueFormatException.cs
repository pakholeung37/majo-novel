#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public class CommandValueFormatException : FormatException
{
    public CommandValueFormatException(string message) : base(message)
    {
    }

    public CommandValueFormatException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
