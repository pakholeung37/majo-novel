#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("screen show", Category = "Direction")]
public sealed partial class ScreenShowCommand : RuntimeCommand<ScreenShowCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public ScreenShowCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor, IDirectionCommandPolicy policy)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var bindings = new Dictionary<LayoutSlotId, ViewId>
        {
            [LayoutSlotId.Main] = scopes.ResolveView(node, arguments.View),
        };
        var transition = policy.ResolveTransition("screen show", arguments.Transition, context);
        var patch = new ScreenPatch(new LayoutId("single"), bindings, transition);
        await beats.SubmitAsync(patch,
            arguments.Transition.Wait.GetValueOrDefault(false)
                ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string View,
        [Group] TransitionArguments Transition);
}

[Command("screen split", Category = "Direction")]
public sealed partial class ScreenSplitCommand : RuntimeCommand<ScreenSplitCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public ScreenSplitCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor, IDirectionCommandPolicy policy)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var bindings = new Dictionary<LayoutSlotId, ViewId>
        {
            [LayoutSlotId.Main] = scopes.ResolveView(node, arguments.Left),
            [new LayoutSlotId("inset")] = scopes.ResolveView(node, arguments.Right),
        };
        var transition = policy.ResolveTransition("screen split", arguments.Transition, context);
        var patch = new ScreenPatch(new LayoutId("split"), bindings, transition)
        {
            Split = new SplitLayout(
                (float)arguments.Divider.GetValueOrDefault(50),
                (float)arguments.Angle.GetValueOrDefault(0),
                arguments.LeftAt.WasProvided ? new DirectionPoint(arguments.LeftAt.Value.X, arguments.LeftAt.Value.Y) : null,
                arguments.RightAt.WasProvided ? new DirectionPoint(arguments.RightAt.Value.X, arguments.RightAt.Value.Y) : null).Validate(),
        };
        await beats.SubmitAsync(patch,
            arguments.Transition.Wait.GetValueOrDefault(false)
                ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Option] string Left,
        [Option] string Right,
        [Option(Minimum = 0)] CommandValue<double> Divider,
        [Option(ValueHint = "degrees")] CommandValue<double> Angle,
        [Option] CommandValue<Godot.Vector2> LeftAt,
        [Option] CommandValue<Godot.Vector2> RightAt,
        [Group] TransitionArguments Transition);
}

[Command("screen pip", Category = "Direction")]
public sealed partial class ScreenPipCommand : RuntimeCommand<ScreenPipCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public ScreenPipCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor, IDirectionCommandPolicy policy)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var bindings = new Dictionary<LayoutSlotId, ViewId>
        {
            [LayoutSlotId.Main] = scopes.ResolveView(node, arguments.Background),
            [new LayoutSlotId("inset")] = scopes.ResolveView(node, arguments.Inset),
        };
        var transition = policy.ResolveTransition("screen pip", arguments.Transition, context);
        var patch = new ScreenPatch(new LayoutId("pip"), bindings, transition);
        await beats.SubmitAsync(patch,
            arguments.Transition.Wait.GetValueOrDefault(false)
                ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Option] string Background,
        [Option] string Inset,
        [Group] TransitionArguments Transition);
}
