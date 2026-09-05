#nullable enable

using System;
using System.Threading.Tasks;

namespace Majo.Runtime.Dialogue.Commands;

public interface IRuntimeCommand
{
    ICommandSchema Schema { get; }

    string Name => Schema.Name;

    string RootName => Schema.RootName;

    ValueTask ExecuteAsync(
        CommandInput input,
        CommandExecutionContext context);
}

public abstract class RuntimeCommand<TArguments> : IRuntimeCommand
    where TArguments : class
{
    public abstract CommandSchema<TArguments> Schema { get; }

    public string Name => Schema.Name;

    public string RootName => Schema.RootName;

    ICommandSchema IRuntimeCommand.Schema => Schema;

    public async ValueTask ExecuteAsync(
        CommandInput input,
        CommandExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        if (!Schema.IsEnvironmentAttached)
        {
            throw new RuntimeCommandException(
                input.CommandName,
                "Command schema has not been attached to a catalog binding environment.",
                recoverable: false,
                phase: CommandFailurePhase.Schema);
        }

        TArguments arguments;
        try
        {
            arguments = Schema.Bind(input);
        }
        catch (RuntimeCommandException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RuntimeCommandException(
                input.CommandName,
                exception.Message,
                exception,
                recoverable: true,
                phase: CommandFailurePhase.Binding);
        }

        try
        {
            await ExecuteAsync(arguments, context);
        }
        catch (RuntimeCommandException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RuntimeCommandException(
                input.CommandName,
                exception.Message,
                exception,
                recoverable: false,
                phase: CommandFailurePhase.Execution);
        }
    }

    protected abstract ValueTask ExecuteAsync(
        TArguments arguments,
        CommandExecutionContext context);
}
