#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class ActorCommandService : IActorCommandService
{
    private readonly IActorManager actors;

    public ActorCommandService(IActorManager actors)
    {
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
    }

    public Task BackAsync(BackCommand command) => BackAsync(
        command,
        CreateDetachedContext());

    public Task BackAsync(
        BackCommand command,
        RuntimeCommandContext context) => ExecuteActorCommandAsync(
        "back",
        async () =>
        {
            ArgumentNullException.ThrowIfNull(command);
            var (actor, created) = await GetOrCreateAsync<IBackgroundActor>(
                command.ActorId,
                context.CancellationToken);
            var operationScheduled = false;
            try
            {
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
                    actor.TargetAppearance,
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

                context.Track(operation);
                operationScheduled = true;
                await WaitIfRequested(operation, command.Wait, context.CancellationToken);
            }
            catch
            {
                if (created && !operationScheduled)
                {
                    actors.Remove(command.ActorId);
                }

                throw;
            }
        });

    public Task CharacterAsync(CharacterCommand command) => CharacterAsync(
        command,
        CreateDetachedContext());

    public Task CharacterAsync(
        CharacterCommand command,
        RuntimeCommandContext context) => ExecuteActorCommandAsync(
        "char",
        async () =>
        {
            ArgumentNullException.ThrowIfNull(command);
            var (actor, created) = await GetOrCreateAsync<ICharacterActor>(
                command.ActorId,
                context.CancellationToken);
            var operationScheduled = false;
            try
            {
                actor.SetAppearance(command.Appearance);
                actor.SetOpacity(1);
                actor.SetVisible(true);
                var operation = actor.ChangePosition(
                    command.Position,
                    command.DurationSeconds,
                    command.OperationId,
                    command.GroupId);
                context.Track(operation);
                operationScheduled = true;
                await WaitIfRequested(operation, command.Wait, context.CancellationToken);
            }
            catch
            {
                if (created && !operationScheduled)
                {
                    actors.Remove(command.ActorId);
                }

                throw;
            }
        });

    public Task HideAsync(HideCommand command) => HideAsync(
        command,
        CreateDetachedContext());

    public Task HideAsync(
        HideCommand command,
        RuntimeCommandContext context) => ExecuteActorCommandAsync(
        "hide",
        () =>
        {
            ArgumentNullException.ThrowIfNull(command);
            var actor = actors.Get<IActor>(command.ActorId);
            var operation = actor.Hide(
                command.DurationSeconds,
                command.OperationId,
                command.GroupId);
            context.Track(operation);
            return WaitIfRequested(operation, command.Wait, context.CancellationToken);
        });

    private async Task<(TActor Actor, bool Created)> GetOrCreateAsync<TActor>(
        string actorId,
        System.Threading.CancellationToken cancellationToken)
        where TActor : class, IActor
    {
        if (actors.TryGet(actorId, out _))
        {
            return (actors.Get<TActor>(actorId), false);
        }

        var actor = await actors.CreateAsync(actorId, cancellationToken);
        if (actor is not TActor typedActor)
        {
            actors.Remove(actorId);
            throw new ActorException(
                $"Actor '{actorId}' is '{actor.GetType().FullName}', " +
                $"not '{typeof(TActor).FullName}'.");
        }

        return (typedActor, true);
    }

    private static Task WaitIfRequested(
        IOperationHandle operation,
        bool wait,
        System.Threading.CancellationToken cancellationToken) =>
        wait ? operation.Completion.WaitAsync(cancellationToken) : Task.CompletedTask;

    private RuntimeCommandContext CreateDetachedContext() =>
        new(actors, null, null, System.Threading.CancellationToken.None);

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RuntimeCommandException(commandName, exception.Message, exception);
        }
    }
}
