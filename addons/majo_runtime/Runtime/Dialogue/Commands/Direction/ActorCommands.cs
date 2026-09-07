#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("character", Category = "Direction")]
[AtLeastOne(nameof(Arguments.Appearance), nameof(Arguments.At), nameof(Arguments.Scale), nameof(Arguments.Z))]
public sealed partial class CharacterCommand : RuntimeCommand<CharacterCommand.Arguments>
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

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage.GetValueOrDefault("main"));
        var commandName = arguments.Appearance.WasProvided
            ? "character appearance"
            : "character";
        var transition = policy.ResolveTransition(commandName, arguments.Transition, context);
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
        [Primary] string Actor,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Option] CommandValue<string> Appearance,
        [Option] CommandValue<string> At,
        [Option(Minimum = 0.001)] CommandValue<double> Scale,
        [Option] CommandValue<int> Z,
        [Group] TransitionArguments Transition);
}

[Command("leave", Category = "Direction")]
public sealed partial class LeaveCommand : RuntimeCommand<LeaveCommand.Arguments>
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

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage.GetValueOrDefault("main"));
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
        [Primary] string Actor,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Group] TransitionArguments Transition);
}
