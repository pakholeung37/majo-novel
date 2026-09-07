#nullable enable

using System;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CommandRegistrationContext : IRuntimeCommandRegistrar
{
    public CommandRegistrationContext(
        IRuntimeCommandRegistrar commands,
        ICommandConverterRegistrar converters,
        ICommandParameterGroupRegistrar groups)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Converters = converters ?? throw new ArgumentNullException(nameof(converters));
        Groups = groups ?? throw new ArgumentNullException(nameof(groups));
    }

    public IRuntimeCommandRegistrar Commands { get; }

    public ICommandConverterRegistrar Converters { get; }

    public ICommandParameterGroupRegistrar Groups { get; }

    public IRuntimeCommandRegistrar Add(string name,
        Func<Core.IServiceResolver, IRuntimeCommand> factory,
        string providerId, string? version = null) =>
        Commands.Add(name, factory, providerId, version);
}
