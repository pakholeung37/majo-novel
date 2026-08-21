#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class RuntimeCommandService : IRuntimeCommandService
{
    private readonly IActorManager actors;
    private readonly IOperationScheduler operations;

    public RuntimeCommandService(
        IActorManager actors,
        IOperationScheduler operations)
    {
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public Task BackAsync(BackCommand command) => ExecuteActorCommandAsync(
        "back",
        () =>
        {
            ArgumentNullException.ThrowIfNull(command);
            var (actor, created) = GetOrCreate<IBackgroundActor>(command.ActorId);
            IOperationHandle operation;
            if (created)
            {
                actor.SetAppearance(command.Appearance);
                actor.SetOpacity(0);
                operation = actor.Show(
                    command.DurationSeconds,
                    command.OperationId,
                    command.GroupId);
            }
            else if (string.Equals(
                actor.Appearance,
                command.Appearance,
                StringComparison.Ordinal))
            {
                actor.SetVisible(true);
                operation = actor.ChangeOpacity(
                    1,
                    command.DurationSeconds,
                    command.OperationId,
                    command.GroupId);
            }
            else
            {
                actor.SetVisible(true);
                actor.SetOpacity(1);
                operation = actor.CrossFadeAppearance(
                    command.Appearance,
                    command.DurationSeconds,
                    command.OperationId,
                    command.GroupId);
            }

            return WaitIfRequested(operation, command.Wait);
        });

    public Task CharacterAsync(CharacterCommand command) => ExecuteActorCommandAsync(
        "char",
        () =>
        {
            ArgumentNullException.ThrowIfNull(command);
            var (actor, _) = GetOrCreate<ICharacterActor>(command.ActorId);
            actor.SetAppearance(command.Appearance);
            actor.SetVisible(true);
            var operation = actor.ChangePosition(
                command.Position,
                command.DurationSeconds,
                command.OperationId,
                command.GroupId);
            return WaitIfRequested(operation, command.Wait);
        });

    public Task HideAsync(HideCommand command) => ExecuteActorCommandAsync(
        "hide",
        () =>
        {
            ArgumentNullException.ThrowIfNull(command);
            var actor = actors.Get<IActor>(command.ActorId);
            var operation = actor.Hide(
                command.DurationSeconds,
                command.OperationId,
                command.GroupId);
            return WaitIfRequested(operation, command.Wait);
        });

    public Task AwaitAsync(AwaitCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Complete
            ? operations.CompleteAsync(command.Target)
            : operations.WaitAsync(command.Target);
    }

    public Task CancelAsync(CancelCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return operations.CancelAsync(command.Target);
    }

    private (TActor Actor, bool Created) GetOrCreate<TActor>(string actorId)
        where TActor : class, IActor
    {
        if (actors.TryGet(actorId, out _))
        {
            return (actors.Get<TActor>(actorId), false);
        }

        actors.Create(actorId);
        return (actors.Get<TActor>(actorId), true);
    }

    private static Task WaitIfRequested(IOperationHandle operation, bool wait) =>
        wait ? operation.Completion : Task.CompletedTask;

    private static async Task ExecuteActorCommandAsync(
        string commandName,
        Func<Task> execute)
    {
        try
        {
            await execute();
        }
        catch (RuntimeCommandException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RuntimeCommandException(
                commandName,
                exception.Message,
                exception);
        }
    }
}
