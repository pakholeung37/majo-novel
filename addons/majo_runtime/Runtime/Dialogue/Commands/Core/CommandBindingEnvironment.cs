#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CommandBindingEnvironment
{
    public static CommandBindingEnvironment Default { get; } = new(new CommandValueConverterRegistry());

    public CommandBindingEnvironment(
        CommandValueConverterRegistry converters,
        IReadOnlyDictionary<string, ICommandParameterGroup>? groups = null)
    {
        Converters = converters ?? throw new ArgumentNullException(nameof(converters));
        Groups = groups ?? new ReadOnlyDictionary<string, ICommandParameterGroup>(
            new Dictionary<string, ICommandParameterGroup>(StringComparer.OrdinalIgnoreCase));
    }

    public CommandBindingEnvironment(
        CommandValueConverterRegistry converters,
        CommandParameterGroupRegistry? groupRegistry)
        : this(converters, groupRegistry?.Groups)
    {
    }

    public CommandValueConverterRegistry Converters { get; }

    public IReadOnlyDictionary<string, ICommandParameterGroup> Groups { get; }
}
