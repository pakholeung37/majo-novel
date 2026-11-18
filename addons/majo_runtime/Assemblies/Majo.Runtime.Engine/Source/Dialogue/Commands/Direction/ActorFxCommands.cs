#nullable enable

using System.Threading.Tasks;
using Majo.Runtime.Direction.Actors;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("character fx", Category = "Direction")]
public sealed partial class CharacterFxCommand : RuntimeCommand<CharacterFxCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    private readonly IDirectionCommandPolicy policy;
    public CharacterFxCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodes, IDirectionCommandPolicy policy)
    { this.beats = beats; this.scopes = scopes; this.nodes = nodes; this.policy = policy; }
    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var duration = arguments.Duration;
        var transition = policy.ResolveMotionTransition("character fx", TimingArguments.Create(duration: duration, wait: arguments.Wait),
            DirectionTransitionId.Move, context);
        var target = new ActorFxTarget(scopes.ResolveStage(nodes.RequireNode(), arguments.Stage.GetValueOrDefault("main")),
            ActorFxSlot.Instance, new ActorId(arguments.Actor));
        var cue = new ActorFxCue(target, DirectionActorKind.Character, arguments.Effect, transition.DurationSeconds,
            (float)arguments.Strength.GetValueOrDefault(8), arguments.Axis.GetValueOrDefault(ActorShakeAxis.All), arguments.Frequency.GetValueOrDefault(20));
        await beats.SubmitAsync(cue, arguments.Wait.GetValueOrDefault(false)
            ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit, context.CancellationToken);
    }
    public sealed record Arguments(
        [Primary] string Actor,
        [Option] string Effect,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Option(Minimum = 0, ValueHint = "seconds")] CommandValue<double> Duration,
        [Option(Minimum = 0, DefaultText = "8", ValueHint = "Stage reference pixels")] CommandValue<double> Strength,
        [Option(DefaultText = "all")] CommandValue<ActorShakeAxis> Axis,
        [Option(Minimum = 1, DefaultText = "20")] CommandValue<int> Frequency,
        [Option(DefaultText = "false")] CommandValue<bool> Wait);
}

[Command("object fx", Category = "Direction")]
public sealed partial class ObjectFxCommand : RuntimeCommand<ObjectFxCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    private readonly IDirectionCommandPolicy policy;
    public ObjectFxCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodes, IDirectionCommandPolicy policy)
    { this.beats = beats; this.scopes = scopes; this.nodes = nodes; this.policy = policy; }
    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var duration = arguments.Duration;
        var transition = policy.ResolveMotionTransition("object fx", TimingArguments.Create(duration: duration, wait: arguments.Wait),
            DirectionTransitionId.Move, context);
        var target = new ActorFxTarget(scopes.ResolveStage(nodes.RequireNode(), arguments.Stage.GetValueOrDefault("main")),
            ActorFxSlot.Instance, new ActorId(arguments.Actor));
        var cue = new ActorFxCue(target, DirectionActorKind.SceneObject, arguments.Effect, transition.DurationSeconds,
            (float)arguments.Strength.GetValueOrDefault(8), arguments.Axis.GetValueOrDefault(ActorShakeAxis.All), arguments.Frequency.GetValueOrDefault(20));
        await beats.SubmitAsync(cue, arguments.Wait.GetValueOrDefault(false)
            ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit, context.CancellationToken);
    }
    public sealed record Arguments(
        [Primary] string Actor,
        [Option] string Effect,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Option(Minimum = 0, ValueHint = "seconds")] CommandValue<double> Duration,
        [Option(Minimum = 0, DefaultText = "8", ValueHint = "Stage reference pixels")] CommandValue<double> Strength,
        [Option(DefaultText = "all")] CommandValue<ActorShakeAxis> Axis,
        [Option(Minimum = 1, DefaultText = "20")] CommandValue<int> Frequency,
        [Option(DefaultText = "false")] CommandValue<bool> Wait);
}

[Command("backdrop fx", Category = "Direction")]
public sealed partial class BackdropFxCommand : RuntimeCommand<BackdropFxCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    private readonly IDirectionCommandPolicy policy;
    public BackdropFxCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodes, IDirectionCommandPolicy policy)
    { this.beats = beats; this.scopes = scopes; this.nodes = nodes; this.policy = policy; }
    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var duration = arguments.Duration;
        var transition = policy.ResolveMotionTransition("backdrop fx", TimingArguments.Create(duration: duration, wait: arguments.Wait),
            DirectionTransitionId.Move, context);
        var target = new ActorFxTarget(scopes.ResolveStage(nodes.RequireNode(), arguments.Stage.GetValueOrDefault("main")),
            ActorFxSlot.Backdrop);
        var cue = new ActorFxCue(target, DirectionActorKind.Character, arguments.Effect, transition.DurationSeconds,
            (float)arguments.Strength.GetValueOrDefault(8), arguments.Axis.GetValueOrDefault(ActorShakeAxis.All), arguments.Frequency.GetValueOrDefault(20));
        await beats.SubmitAsync(cue, arguments.Wait.GetValueOrDefault(false)
            ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit, context.CancellationToken);
    }
    public sealed record Arguments(
        [Primary] string Effect,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Option(Minimum = 0, ValueHint = "seconds")] CommandValue<double> Duration,
        [Option(Minimum = 0, DefaultText = "8", ValueHint = "Stage reference pixels")] CommandValue<double> Strength,
        [Option(DefaultText = "all")] CommandValue<ActorShakeAxis> Axis,
        [Option(Minimum = 1, DefaultText = "20")] CommandValue<int> Frequency,
        [Option(DefaultText = "false")] CommandValue<bool> Wait);
}
