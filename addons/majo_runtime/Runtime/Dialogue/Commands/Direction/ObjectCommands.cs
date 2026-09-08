#nullable enable

using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Majo.Runtime.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("object", Category = "Direction")]
public sealed partial class ObjectCommand : RuntimeCommand<ObjectCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;
    private readonly IDirectionTransactionCoordinator? direction;

    public ObjectCommand(
        IDirectionBeatRuntime beats,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor,
        IDirectionCommandPolicy policy,
        IDirectionTransactionCoordinator? direction = null)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.direction = direction;
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage.GetValueOrDefault("main"));
        ActorState? existing = null;
        direction?.State.Stages.GetValueOrDefault(stageId)?.Actors.TryGetValue(new ActorId(arguments.Actor), out existing);
        var commandName = existing is null || !existing.Visible ? "object" :
            arguments.Appearance.WasProvided ? "object appearance" : "object space";
        var transition = policy.ResolveTransition(commandName, arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        DirectionPoint? position = arguments.At.WasProvided
            ? scopes.ResolvePosition(node, arguments.At.Value!)
            : null;

        await beats.SubmitAsync(
            new ActorPatch(
                stageId,
                new ActorId(arguments.Actor),
                position.HasValue ? DirectionValue.From(position.Value) : default,
                arguments.Appearance.WasProvided ? DirectionValue.From<string?>(arguments.Appearance.Value) : default,
                arguments.Scale.WasProvided ? DirectionValue.From((float)arguments.Scale.Value) : default,
                arguments.Z.WasProvided ? DirectionValue.From(arguments.Z.Value) : default,
                transition)
            {
                Kind = DirectionActorKind.SceneObject,
                DefinitionId = arguments.Asset.Value,
                RotationDegrees = arguments.Rotation.WasProvided ? DirectionValue.From((float)arguments.Rotation.Value) : default
            },
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Actor,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Option] CommandValue<string> Appearance,
        [Option] CommandValue<PositionRef> At,
        [Option(Minimum = 0.001)] CommandValue<double> Scale,
        [Option] CommandValue<int> Z,
        [Group] TransitionArguments Transition,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation,
        [Option] CommandValue<string> Asset);
}

[Command("object hide", Category = "Direction")]
public sealed partial class ObjectHideCommand : RuntimeCommand<ObjectHideCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public ObjectHideCommand(
        IDirectionBeatRuntime beats,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor,
        IDirectionCommandPolicy policy)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage.GetValueOrDefault("main"));
        var transition = policy.ResolveTransition("object hide", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new ActorHidePatch(
                stageId,
                new ActorId(arguments.Actor),
                DirectionActorKind.SceneObject,
                transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Actor,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Group] TransitionArguments Transition);
}
