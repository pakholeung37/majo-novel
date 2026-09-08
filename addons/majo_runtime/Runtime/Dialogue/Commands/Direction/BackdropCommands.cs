#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Resources;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

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
