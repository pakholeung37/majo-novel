#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class StageDefineCommand : RuntimeCommand<StageDefineCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("stage define", category: "Direction", usage: "stage define <id>")
            .Primary<string>("stage")
            .Build(static values => new Arguments(values.Required<string>("stage")));

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

    public sealed record Arguments(string Stage);
}

public sealed class StageShowCommand : RuntimeCommand<StageShowCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "stage show",
                category: "Direction",
                usage: "stage show <id> [via:<cut|dissolve>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Primary<string>("stage")
            .Use("transition", CommandParameterGroups.Transition())
            .Build(static values => new Arguments(
                values.Required<string>("stage"),
                values.Group<TransitionArguments>("transition")));

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
        string Stage,
        TransitionArguments Transition);
}

public sealed class StageClearCommand : RuntimeCommand<StageClearCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "stage clear",
                category: "Direction",
                usage: "stage clear <id> [via:<cut|fade|dissolve>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Primary<string>("stage")
            .Use("transition", CommandParameterGroups.Transition())
            .Build(static values => new Arguments(
                values.Required<string>("stage"),
                values.Group<TransitionArguments>("transition")));

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
        string Stage,
        TransitionArguments Transition);
}

public sealed class StageReleaseCommand : RuntimeCommand<StageReleaseCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("stage release", category: "Direction", usage: "stage release <id>")
            .Primary<string>("stage")
            .Build(static values => new Arguments(values.Required<string>("stage")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage);

        await beats.SubmitAsync(
            new StageReleasePatch(stageId, DirectionTransitionSpec.Cut),
            DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(string Stage);
}

public sealed class BackdropCommand : RuntimeCommand<BackdropCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionResourceKeyResolver resources;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public BackdropCommand(
        IDirectionBeatRuntime beats,
        IDirectionResourceKeyResolver resources,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor,
        IDirectionCommandPolicy policy)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "backdrop",
                category: "Direction",
                usage: "backdrop <resource> [stage:<id>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Primary<string>("resource")
            .Optional<string>("stage", contractDefault: "main", hasContractDefault: true)
            .Use("transition", CommandParameterGroups.Transition())
            .Build(static values => new Arguments(
                values.Required<string>("resource"),
                values.Optional<string>("stage").GetValueOrDefault("main"),
                values.Group<TransitionArguments>("transition")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage);
        var backdropState = new BackdropState(resources.ResolveBackdrop(arguments.Resource));
        var transition = policy.ResolveTransition("backdrop", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new BackdropPatch(stageId, backdropState, transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        string Resource,
        string Stage,
        TransitionArguments Transition);
}
