#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("view define", Category = "Direction")]
public sealed partial class ViewDefineCommand : RuntimeCommand<ViewDefineCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;

    public ViewDefineCommand(
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
        var viewId = scopes.ResolveView(node, arguments.View);
        var stageId = scopes.ResolveStage(node, arguments.Stage);

        await beats.SubmitAsync(
            new ViewCreatePatch(viewId, stageId, DirectionTransitionSpec.Cut),
            DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments([Primary] string View, [Option] string Stage);
}

