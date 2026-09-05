#nullable enable

using System;
using System.Collections.Generic;

namespace Majo.Runtime.Dialogue.Commands;

public enum CommandParameterSetRule
{
    AtMostOne,
    ExactlyOne,
    AtLeastOne,
}

public sealed record CommandParameterSetSpec(
    CommandParameterSetRule Rule,
    IReadOnlyList<string> Parameters);

public sealed record CommandParameterSpec(
    string Name,
    int Order,
    bool Primary,
    bool Required,
    Type ValueType,
    string ValueHint,
    object? ContractDefault = null,
    double Minimum = double.NaN);

public sealed class CommandSchemaCompositionContext
{
    public CommandSchemaCompositionContext(
        string commandName,
        IReadOnlySet<string> existingParameterNames)
    {
        CommandName = commandName;
        ExistingParameterNames = existingParameterNames;
    }

    public string CommandName { get; }

    public IReadOnlySet<string> ExistingParameterNames { get; }
}

public interface ICommandParameterGroup
{
    string Id { get; }

    IReadOnlyList<CommandParameterSpec> Parameters { get; }

    void Validate(CommandSchemaCompositionContext context);
}

public interface ICommandParameterGroup<out TValue> : ICommandParameterGroup
{
    TValue Bind(RawCommandArguments raw, CommandBindingEnvironment environment);
}

public sealed class DelegateCommandParameterGroup<TValue> : ICommandParameterGroup<TValue>
{
    private readonly Func<RawCommandArguments, CommandBindingEnvironment, TValue> binder;

    public DelegateCommandParameterGroup(
        string id,
        IReadOnlyList<CommandParameterSpec> parameters,
        Func<RawCommandArguments, CommandBindingEnvironment, TValue> binder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Parameters = parameters ?? Array.Empty<CommandParameterSpec>();
        this.binder = binder ?? throw new ArgumentNullException(nameof(binder));
    }

    public string Id { get; }

    public IReadOnlyList<CommandParameterSpec> Parameters { get; }

    public void Validate(CommandSchemaCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var parameter in Parameters)
        {
            if (context.ExistingParameterNames.Contains(parameter.Name))
            {
                throw new RuntimeCommandException(
                    context.CommandName,
                    $"Group '{Id}' defines duplicate parameter '{parameter.Name}'.",
                    phase: CommandFailurePhase.Schema);
            }
        }
    }

    public TValue Bind(RawCommandArguments raw, CommandBindingEnvironment environment) =>
        binder(raw, environment);
}
