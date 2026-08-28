#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
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
            var operationScheduled = false;
            try
            {
                await actor.PrepareAppearanceAsync(
                    command.Appearance,
                    context.CancellationToken);
                actor.SetSlot(command.Slot);
                var competingActors = actors.Actors
                    .OfType<IBackgroundActor>()
                    .Where(candidate =>
                        !ReferenceEquals(candidate, actor) &&
                        string.Equals(candidate.Slot, actor.Slot, StringComparison.Ordinal) &&
                        (candidate.Visible || candidate.TargetVisible))
                    .ToArray();
                BringBackgroundToFront(actor);
                IOperationHandle appearanceOperation;
                if (created || (!actor.Visible && !actor.TargetVisible))
                {
                    actor.SetAppearance(command.Appearance);
                    actor.SetOpacity(0);
                    appearanceOperation = actor.Show(
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
                    appearanceOperation = actor.ChangeOpacity(
                        1,
                        command.DurationSeconds,
                        command.OperationId,
                        command.GroupId);
                }
                else
                {
                    actor.SetVisible(true);
                    actor.SetOpacity(1);
                    appearanceOperation = actor.CrossFadeAppearance(
                        command.Appearance,
                        command.DurationSeconds,
                        command.OperationId,
                        command.GroupId);
                }

                var transitionOperations = new List<IOperationHandle>(
                    competingActors.Length + 1)
                {
                    context.Track(appearanceOperation),
                };
                operationScheduled = true;
                foreach (var competingActor in competingActors)
                {
                    transitionOperations.Add(context.Track(competingActor.Hide(
                        command.DurationSeconds,
                        groupId: command.GroupId)));
                }

                if (competingActors.Length > 0)
                {
                    logger.Debug(
                        "actor",
                        "background.slot_transition",
                        $"Background '{actor.Id}' replaced {competingActors.Length} actor(s) " +
                        $"in slot '{actor.Slot}'.",
                        properties: new Dictionary<string, object?>
                        {
                            ["actorId"] = actor.Id,
                            ["slot"] = actor.Slot,
                            ["replacedActorIds"] = competingActors
                                .Select(candidate => candidate.Id)
                                .ToArray(),
                        });
                }

                await WaitIfRequested(
                    transitionOperations,
                    command.Wait,
                    context.CancellationToken);
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

                if (command.ZIndex is { } zIndex)
                {
                    actor.SetZIndex(zIndex);
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
        CancellationToken cancellationToken) =>
        wait ? operation.Completion.WaitAsync(cancellationToken) : Task.CompletedTask;

    private static Task WaitIfRequested(
        IReadOnlyCollection<IOperationHandle> operations,
        bool wait,
        CancellationToken cancellationToken) => wait
            ? Task.WhenAll(operations.Select(operation => operation.Completion))
                .WaitAsync(cancellationToken)
            : Task.CompletedTask;

    private static void BringBackgroundToFront(IBackgroundActor actor)
    {
        var node = actor.SceneNode;
        if (node is null || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        var parent = node.GetParent();
        if (parent is not null)
        {
            parent.MoveChild(node, parent.GetChildCount() - 1);
        }
    }

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
