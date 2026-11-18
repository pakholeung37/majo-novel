#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

public interface IRuntimeCommand
{
    string Name => Definition.Name;

    string RootName => Definition.RootName;

    IReadOnlyList<string> Path => Definition.Path;

    string Usage => Definition.Usage;

    ICommandDefinition Definition { get; }

    ValueTask ExecuteAsync(
        CommandInput input,
        CommandExecutionContext context);

    internal IBoundCommand CreateBoundCommand(
        BindingResolutionContext resolutionContext,
        BoundParameterSlot[] slots);
}

public abstract class RuntimeCommand<TArguments> : IRuntimeCommand
    where TArguments : class
{
    public abstract CommandDefinition<TArguments> Definition { get; }

    ICommandDefinition IRuntimeCommand.Definition => Definition;

    public string Name => Definition.Name;

    public string RootName => Definition.RootName;

    public IReadOnlyList<string> Path => Definition.Path;

    public virtual ValueTask ExecuteAsync(
        CommandInput input,
        CommandExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        throw new RuntimeCommandException(
            input.CommandName,
            "Direct execution of unbound command is not supported. Use catalog-bound command execution.",
            recoverable: false,
            phase: CommandFailurePhase.Schema);
    }

    IBoundCommand IRuntimeCommand.CreateBoundCommand(
        BindingResolutionContext resolutionContext,
        BoundParameterSlot[] slots)
    {
        var factory = Definition.BinderFactory(resolutionContext);
        return new BoundCommand<TArguments>(
            this,
            Definition,
            factory,
            slots,
            CommandContractValidation.ResolveSets(Definition),
            Definition.Validators);
    }

    internal ValueTask InvokeExecuteAsync(
        TArguments arguments,
        CommandExecutionContext context) =>
        ExecuteAsync(arguments, context);

    protected abstract ValueTask ExecuteAsync(
        TArguments arguments,
        CommandExecutionContext context);
}
