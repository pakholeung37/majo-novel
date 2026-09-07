#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Resources;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("stage define", Category = "Direction")]
public sealed partial class StageDefineCommand : RuntimeCommand<StageDefineCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;

    public StageDefineCommand(
        IDirectionBeatRuntime beats,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage);
        var defaultView = scopes.ResolveDefaultView(node, arguments.Stage);

        await beats.SubmitAsync(
            new StageCreatePatch(
                stageId,
                defaultView,
                DirectionTransitionSpec.Cut),
            DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments([Primary] string Stage);
}

[Command("stage show", Category = "Direction")]
public sealed partial class StageShowCommand : RuntimeCommand<StageShowCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public StageShowCommand(
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
        var defaultView = scopes.ResolveDefaultView(node, arguments.Stage);
        var transition = policy.ResolveTransition("stage show", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new ScreenPatch(
                LayoutId.SingleLayout,
                new Dictionary<LayoutSlotId, ViewId>
                {
                    [LayoutSlotId.Main] = defaultView,
                },
                transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Stage,
        [Group] TransitionArguments Transition);
}

[Command("stage clear", Category = "Direction")]
public sealed partial class StageClearCommand : RuntimeCommand<StageClearCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public StageClearCommand(
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
        var stageId = scopes.ResolveStage(node, arguments.Stage);
        var transition = policy.ResolveTransition("stage clear", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new StageClearPatch(stageId, transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Stage,
        [Group] TransitionArguments Transition);
}

[Command("stage release", Category = "Direction")]
public sealed partial class StageReleaseCommand : RuntimeCommand<StageReleaseCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;

    public StageReleaseCommand(
        IDirectionBeatRuntime beats,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage);

        await beats.SubmitAsync(
            new StageReleasePatch(stageId, DirectionTransitionSpec.Cut),
            DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments([Primary] string Stage);
}

[Command("backdrop", Category = "Direction")]
public sealed partial class BackdropCommand : RuntimeCommand<BackdropCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly string backgroundsPath;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public BackdropCommand(
        IDirectionBeatRuntime beats,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor,
        IDirectionCommandPolicy policy,
        string backgroundsPath = ResourceConfiguration.DefaultBackgroundsPath)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.backgroundsPath = backgroundsPath ?? throw new ArgumentNullException(nameof(backgroundsPath));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage.GetValueOrDefault("main"));
        var backdropState = new BackdropState(arguments.Resource.ToResourceKey(backgroundsPath));
        var transition = policy.ResolveTransition("backdrop", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new BackdropPatch(stageId, backdropState, transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] BackdropId Resource,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Group] TransitionArguments Transition);
}
