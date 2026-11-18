#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CommandInput
{
    public CommandInput(string commandName, IReadOnlyList<string>? rawArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        CommandName = commandName;
        RawArguments = rawArguments ?? Array.Empty<string>();
    }

    public string CommandName { get; }

    public IReadOnlyList<string> RawArguments { get; }
}
