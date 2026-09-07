#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

[Command("shot use", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
public sealed partial class ShotUseCommand : RuntimeCommand<ShotUseCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public ShotUseCommand(
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
        var patch = scopes.ResolveShot(node, arguments.Alias);
        if (arguments.Stage.WasProvided || arguments.View.WasProvided)
        {
            var targetView = !string.IsNullOrWhiteSpace(arguments.Stage.Value)
                ? scopes.ResolveDefaultView(node, arguments.Stage.Value)
                : scopes.ResolveView(node, string.IsNullOrWhiteSpace(arguments.View.Value) ? "main" : arguments.View.Value);
            patch = patch with { ViewId = targetView };
        }

        var transition = policy.ResolveTransition("shot use", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);
        patch = patch with { Transition = transition };

        await beats.SubmitAsync(
            patch,
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Alias,
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Group] TransitionArguments Transition);
}

[Command("layout", Category = "Direction")]
public sealed partial class LayoutCommand : RuntimeCommand<LayoutCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public LayoutCommand(
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
        var bindings = new Dictionary<LayoutSlotId, ViewId>
        {
            [LayoutSlotId.Main] = scopes.ResolveView(node, arguments.Main.GetValueOrDefault("main")),
        };

        if (arguments.Inset.WasProvided && !string.IsNullOrWhiteSpace(arguments.Inset.Value))
        {
            bindings[new LayoutSlotId("inset")] = scopes.ResolveView(node, arguments.Inset.Value);
        }

        var transition = policy.ResolveTransition("layout", arguments.Transition, context);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new ScreenPatch(
                new LayoutId(arguments.Layout),
                bindings,
                transition),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary] string Layout,
        [Option] CommandValue<string> Main,
        [Option] CommandValue<string> Inset,
        [Group] TransitionArguments Transition);
}

[Command("import", Category = "Direction")]
public sealed partial class DirectionImportCommand : RuntimeCommand<DirectionImportCommand.Arguments>
{
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;

    public DirectionImportCommand(
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor)
    {
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
    }

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var parts = arguments.Source.Split(':', 2);
        var sourceNode = parts[0];
        var sourceSymbol = parts.Length == 2 ? parts[1] : string.Empty;

        if (arguments.Kind.Equals("stage", StringComparison.OrdinalIgnoreCase))
        {
            scopes.ImportStage(node, arguments.Alias, sourceNode, sourceSymbol);
        }
        else if (arguments.Kind.Equals("view", StringComparison.OrdinalIgnoreCase))
        {
            scopes.ImportView(node, arguments.Alias, sourceNode, sourceSymbol);
        }
        else
        {
            throw new DirectionReferenceException($"Unsupported import kind '{arguments.Kind}'.");
        }

        return ValueTask.CompletedTask;
    }

    public sealed record Arguments(
        [Primary] string Kind,
        [Option] string Alias,
        [Option] string Source);
}

[Command("mark", Category = "Direction")]
public sealed partial class DirectionMarkCommand : RuntimeCommand<DirectionMarkCommand.Arguments>
{
    private readonly IDirectionPositionResolver positions;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;

    public DirectionMarkCommand(
        IDirectionPositionResolver positions,
        IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodeAccessor)
    {
        this.positions = positions ?? throw new ArgumentNullException(nameof(positions));
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
    }

    protected override ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var position = positions.Resolve(arguments.At);
        try
        {
            scopes.DefineMark(node, arguments.Name, position);
        }
        catch (DirectionReferenceException)
        {
            if (!scopes.ResolveMark(node, arguments.Name).Equals(position))
            {
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    public sealed record Arguments([Primary] string Name, [Option] string At);
}

[Command("curtain", Category = "Direction")]
public sealed partial class CurtainCommand : RuntimeCommand<CurtainCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDialogueNodeAccessor nodeAccessor;

    public CurtainCommand(
        IDirectionBeatRuntime beats,
        IDialogueNodeAccessor nodeAccessor)
    {
        this.beats = beats ?? throw new ArgumentNullException(nameof(beats));
        this.nodeAccessor = nodeAccessor ?? throw new ArgumentNullException(nameof(nodeAccessor));
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var duration = arguments.Duration.GetValueOrDefault(0.35);
        var r = arguments.R.GetValueOrDefault(0.0);
        var g = arguments.G.GetValueOrDefault(0.0);
        var b = arguments.B.GetValueOrDefault(0.0);
        var transition = new DirectionTransitionSpec(
            DirectionTransitionId.Fade,
            duration,
            EasingId.Linear).Validate();
        var opacity = arguments.Position == CurtainPosition.Open ? 0f : 1f;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new CurtainPatch(
                new CurtainState(
                    new DirectionColor((float)r, (float)g, (float)b),
                    opacity),
                transition,
                new DirectionSourceLocation(node, 0, 0)),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        [Primary(Name = "state")] CurtainPosition Position,
        [Option(DefaultText = "0.35", Minimum = 0, ValueHint = "seconds")] CommandValue<double> Duration,
        [Option(DefaultText = "0.0", Minimum = 0, ValueHint = "0..1")] CommandValue<double> R,
        [Option(DefaultText = "0.0", Minimum = 0, ValueHint = "0..1")] CommandValue<double> G,
        [Option(DefaultText = "0.0", Minimum = 0, ValueHint = "0..1")] CommandValue<double> B,
        [Group] WaitArguments Wait);
}
