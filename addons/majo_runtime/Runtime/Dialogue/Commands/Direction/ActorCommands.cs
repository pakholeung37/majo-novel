#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CharacterCommand : RuntimeCommand<CharacterCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionPositionResolver positions;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public CharacterCommand(
        IDirectionBeatRuntime beats,
        IDirectionPositionResolver positions,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor,
        IDirectionCommandPolicy policy)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "character",
                category: "Direction",
                usage: "character <id> [stage:<id>] [appearance:<id>] [at:<mark|x,y>] [scale:<number>] [z:<integer>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Primary<string>("actor")
            .Optional<string>("stage", contractDefault: "main", hasContractDefault: true)
            .Optional<string>("appearance")
            .Optional<string>("at")
            .Optional<double>("scale", minimum: 0.001)
            .Optional<int>("z")
            .Use("transition", CommandParameterGroups.Transition())
            .ParameterSet(CommandParameterSetRule.AtLeastOne, "appearance", "at", "scale", "z")
            .Build(static values => new Arguments(
                values.Required<string>("actor"),
                values.Optional<string>("stage").GetValueOrDefault("main"),
                values.Optional<string>("appearance"),
                values.Optional<string>("at"),
                values.Optional<double>("scale"),
                values.Optional<int>("z"),
                values.Group<TransitionArguments>("transition")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage);
        var transition = policy.ResolveTransition("character", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        DirectionPoint? position = null;
        if (arguments.At.WasProvided && !string.IsNullOrWhiteSpace(arguments.At.Value))
        {
            position = ResolvePosition(arguments.At.Value);
        }

        await beats.SubmitAsync(
            new ActorPatch(
                stageId,
                new ActorId(arguments.Actor),
                position.HasValue ? DirectionValue.From(position.Value) : default,
                arguments.Appearance.WasProvided ? DirectionValue.From<string?>(arguments.Appearance.Value) : default,
                arguments.Scale.WasProvided ? DirectionValue.From((float)arguments.Scale.Value) : default,
                arguments.Z.WasProvided ? DirectionValue.From(arguments.Z.Value) : default,
                transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    private DirectionPoint ResolvePosition(string value)
    {
        try
        {
            return scopes.ResolveMark(nodeAccessor.RequireNode(), value);
        }
        catch (DirectionReferenceException)
        {
            return positions.Resolve(value);
        }
    }

    public sealed record Arguments(
        string Actor,
        string Stage,
        CommandValue<string> Appearance,
        CommandValue<string> At,
        CommandValue<double> Scale,
        CommandValue<int> Z,
        TransitionArguments Transition);
}

public sealed class LeaveCommand : RuntimeCommand<LeaveCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public LeaveCommand(
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "leave",
                category: "Direction",
                usage: "leave <character> [stage:<id>] [via:<cut|fade|dissolve>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Primary<string>("actor")
            .Optional<string>("stage", contractDefault: "main", hasContractDefault: true)
            .Use("transition", CommandParameterGroups.Transition())
            .Build(static values => new Arguments(
                values.Required<string>("actor"),
                values.Optional<string>("stage").GetValueOrDefault("main"),
                values.Group<TransitionArguments>("transition")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage);
        var transition = policy.ResolveTransition("leave", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new ActorLeavePatch(
                stageId,
                new ActorId(arguments.Actor),
                transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        string Actor,
        string Stage,
        TransitionArguments Transition);
}
