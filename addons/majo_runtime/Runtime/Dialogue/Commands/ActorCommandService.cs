#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class ActorCommandService : IActorCommandService
{
    private readonly IActorManager actors;
    private readonly IRuntimeLogger logger;

    public ActorCommandService(
        IActorManager actors,
        IRuntimeLogger? logger = null)
    {
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
        this.logger = logger ?? NullRuntimeLogger.Instance;
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
                new ActorSpawnRequest(
                    command.ActorId,
                    ActorFactoryIds.Background,
                    command.Appearance),
                context.CancellationToken);
            await actor.PrepareAppearanceAsync(
                command.Appearance,
                context.CancellationToken);
            var operationScheduled = false;
            try
            {
                IOperationHandle operation;
                if (created || (!actor.Visible && !actor.TargetVisible))
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
                new ActorSpawnRequest(
                    command.ActorId,
                    ActorFactoryIds.Character,
                    command.Appearance,
                    command.Position),
                context.CancellationToken);
            await actor.PrepareAppearanceAsync(
                command.Appearance,
                context.CancellationToken);
            var operationScheduled = false;
            try
            {
                if (command.Scale is { } scale)
                {
                    actor.SetScale(new Godot.Vector2(scale, scale));
                }

                IOperationHandle appearanceOperation;
                if (created || (!actor.Visible && !actor.TargetVisible))
                {
                    actor.SetAppearance(command.Appearance);
                    actor.SetOpacity(0);
                    appearanceOperation = actor.Show(
                        command.DurationSeconds,
                        command.OperationId,
                        groupId: command.GroupId);
                }
                else if (string.Equals(
                    actor.TargetAppearance,
                    command.Appearance,
                    StringComparison.Ordinal))
                {
                    actor.SetVisible(true);
                    appearanceOperation = actor.ChangeOpacity(
                        1,
                        command.DurationSeconds,
                        command.OperationId,
                        groupId: command.GroupId);
                }
                else
                {
                    appearanceOperation = actor.ChangeAppearance(
                        command.Appearance,
                        command.DurationSeconds,
                        command.OperationId,
                        groupId: command.GroupId);
                }

                if (command.ScenePosition is { } scenePosition)
                {
                    actor.SetScenePosition(scenePosition);
                }
                else
                {
                    actor.SetPosition(command.Position);
                }

                context.Track(appearanceOperation);
                operationScheduled = true;
                if (command.Wait)
                {
                    await appearanceOperation.Completion.WaitAsync(context.CancellationToken);
                }
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
            if (!actors.TryGet(command.ActorId, out _))
            {
                logger.Warning(
                    "actor",
                    "actor.missing_noop",
                    $"Actor '{command.ActorId}' was not active; command 'hide' was skipped.",
                    properties: new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["actorId"] = command.ActorId,
                        ["commandName"] = "hide",
                        ["fallback"] = "no-op",
                    });
                return Task.CompletedTask;
            }

            var actor = actors.Get<IActor>(command.ActorId);
            var operation = actor.Hide(
                command.DurationSeconds,
                command.OperationId,
                command.GroupId);
            context.Track(operation);
            return WaitIfRequested(operation, command.Wait, context.CancellationToken);
        });

    private async Task<(TActor Actor, bool Created)> GetOrCreateAsync<TActor>(
        ActorSpawnRequest request,
        System.Threading.CancellationToken cancellationToken)
        where TActor : class, IActor
    {
        if (actors.TryGet(request.ActorId, out _))
        {
            return (actors.Get<TActor>(request.ActorId), false);
        }

        var actor = await actors.GetOrCreateAsync(request, cancellationToken);
        if (actor is not TActor typedActor)
        {
            throw new ActorException(
                $"Actor '{request.ActorId}' is '{actor.GetType().FullName}', " +
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
        catch (ActorException exception)
        {
            throw new RuntimeCommandException(commandName, exception.Message, exception);
        }
    }
}
