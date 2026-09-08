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
        if (arguments.At.WasProvided || arguments.Origin.WasProvided || arguments.Scale.WasProvided)
        {
            var origin = arguments.Origin.GetValueOrDefault(new Godot.Vector2(0.5f, 0.5f));
            backdropState = backdropState with { Placement = new BackdropPlacement(
                arguments.At.WasProvided ? scopes.ResolvePosition(node, arguments.At.Value!) : new DirectionPoint(50, 50),
                new DirectionPoint(origin.X, origin.Y), (float)arguments.Scale.GetValueOrDefault(1)).Validate() };
        }
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
        [Group] TransitionArguments Transition,
        [Option] CommandValue<PositionRef> At,
        [Option(ValueHint = "x,y")] CommandValue<Godot.Vector2> Origin,
        [Option(Minimum = 0.001)] CommandValue<double> Scale);
}
