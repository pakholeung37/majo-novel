#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("backdrop", Category = "Direction")]
public sealed partial class BackdropCommand : RuntimeCommand<BackdropCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;
    private readonly CameraEnvironmentPresetCatalog environment;

    public BackdropCommand(
        IDirectionBeatRuntime beats,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor,
        IDirectionCommandPolicy policy,
        CameraEnvironmentPresetCatalog? environment = null)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.environment = environment ?? new CameraEnvironmentPresetCatalog();
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var stageId = scopes.ResolveStage(node, arguments.Stage.GetValueOrDefault("main"));
        BackdropPlacement? placement = null;
        if (arguments.At.WasProvided || arguments.Origin.WasProvided || arguments.Scale.WasProvided)
        {
            var origin = arguments.Origin.GetValueOrDefault(new Godot.Vector2(0.5f, 0.5f));
            placement = new BackdropPlacement(
                arguments.At.WasProvided ? scopes.ResolvePosition(node, arguments.At.Value!) : new DirectionPoint(50, 50),
                new DirectionPoint(origin.X, origin.Y), (float)arguments.Scale.GetValueOrDefault(1)).Validate();
        }
        var transition = policy.ResolveTransition("backdrop", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new BackdropIntent(stageId, arguments.Definition, arguments.Appearance.WasProvided ? arguments.Appearance.Value : null,
                arguments.Distance.WasProvided ? DirectionValue.From(environment.ResolveDistance(arguments.Distance.Value!)) : default,
                placement, transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Definition,
        [Option] CommandValue<string> Appearance,
        [Option(DefaultText = "main")] CommandValue<string> Stage,
        [Group] TransitionArguments Transition,
        [Option] CommandValue<PositionRef> At,
        [Option(ValueHint = "x,y")] CommandValue<Godot.Vector2> Origin,
        [Option(Minimum = 0.001)] CommandValue<double> Scale,
        [Option(ValueHint = "flat|near|far|project preset")] CommandValue<string> Distance);
}
