#nullable enable

using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("character move", Category = "Direction")]
[AtLeastOne(nameof(Arguments.At), nameof(Arguments.Scale), nameof(Arguments.Rotation))]
public sealed partial class CharacterMoveCommand : RuntimeCommand<CharacterMoveCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    private readonly IDirectionCommandPolicy policy;

    public CharacterMoveCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodes, IDirectionCommandPolicy policy)
    { this.beats = beats; this.scopes = scopes; this.nodes = nodes; this.policy = policy; }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodes.RequireNode();
        var transition = policy.ResolveMotionTransition("character move", arguments.Timing, DirectionTransitionId.Move, context);
        var patch = new ActorMovePatch(scopes.ResolveStage(node, arguments.Stage.GetValueOrDefault("main")),
            new ActorId(arguments.Actor), DirectionActorKind.Character,
            arguments.At.WasProvided ? DirectionValue.From(scopes.ResolvePosition(node, arguments.At.Value!)) : default,
            arguments.Scale.WasProvided ? DirectionValue.From((float)arguments.Scale.Value) : default,
            arguments.Rotation.WasProvided ? DirectionValue.From((float)arguments.Rotation.Value) : default,
            transition);
        await beats.SubmitAsync(patch, arguments.Timing.Wait.GetValueOrDefault(false)
            ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit, context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Actor,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Option] CommandValue<PositionRef> At,
        [Option(Minimum = 0.001)] CommandValue<double> Scale,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation,
        [Group] TimingArguments Timing);
}

[Command("object move", Category = "Direction")]
[AtLeastOne(nameof(Arguments.At), nameof(Arguments.Scale), nameof(Arguments.Rotation))]
public sealed partial class ObjectMoveCommand : RuntimeCommand<ObjectMoveCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    private readonly IDirectionCommandPolicy policy;

    public ObjectMoveCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodes, IDirectionCommandPolicy policy)
    { this.beats = beats; this.scopes = scopes; this.nodes = nodes; this.policy = policy; }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodes.RequireNode();
        var transition = policy.ResolveMotionTransition("object move", arguments.Timing, DirectionTransitionId.Move, context);
        var patch = new ActorMovePatch(scopes.ResolveStage(node, arguments.Stage.GetValueOrDefault("main")),
            new ActorId(arguments.Actor), DirectionActorKind.SceneObject,
            arguments.At.WasProvided ? DirectionValue.From(scopes.ResolvePosition(node, arguments.At.Value!)) : default,
            arguments.Scale.WasProvided ? DirectionValue.From((float)arguments.Scale.Value) : default,
            arguments.Rotation.WasProvided ? DirectionValue.From((float)arguments.Rotation.Value) : default,
            transition);
        await beats.SubmitAsync(patch, arguments.Timing.Wait.GetValueOrDefault(false)
            ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit, context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Actor,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Option] CommandValue<PositionRef> At,
        [Option(Minimum = 0.001)] CommandValue<double> Scale,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation,
        [Group] TimingArguments Timing);
}
