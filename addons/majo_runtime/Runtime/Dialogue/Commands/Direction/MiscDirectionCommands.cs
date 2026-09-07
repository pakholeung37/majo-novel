#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Stage;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class ShotUseCommand : RuntimeCommand<ShotUseCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "shot use",
                category: "Direction",
                usage: "shot use <alias> [stage:<id>|view:<id>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Primary<string>("alias")
            .Optional<string>("stage")
            .Optional<string>("view")
            .Use("transition", CommandParameterGroups.Transition())
            .ParameterSet(CommandParameterSetRule.AtMostOne, "stage", "view")
            .Build(static values => new Arguments(
                values.Required<string>("alias"),
                values.Optional<string>("stage"),
                values.Optional<string>("view"),
                values.Group<TransitionArguments>("transition")));

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
        string Alias,
        CommandValue<string> Stage,
        CommandValue<string> View,
        TransitionArguments Transition);
}

public sealed class LayoutCommand : RuntimeCommand<LayoutCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "layout",
                category: "Direction",
                usage: "layout <single|split|pip> <slot:view> ... [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Primary<string>("layout")
            .Optional<string>("main")
            .Optional<string>("inset")
            .Use("transition", CommandParameterGroups.Transition())
            .Build(static values => new Arguments(
                values.Required<string>("layout"),
                values.Optional<string>("main"),
                values.Optional<string>("inset"),
                values.Group<TransitionArguments>("transition")));

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
        string Layout,
        CommandValue<string> Main,
        CommandValue<string> Inset,
        TransitionArguments Transition);
}

public sealed class DirectionImportCommand : RuntimeCommand<DirectionImportCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "import",
                category: "Direction",
                usage: "import <stage> alias:<alias> source:<node:symbol>")
            .Primary<string>("kind")
            .Required<string>("alias")
            .Required<string>("source")
            .Build(static values => new Arguments(
                values.Required<string>("kind"),
                values.Required<string>("alias"),
                values.Required<string>("source")));

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
        string Kind,
        string Alias,
        string Source);
}

public sealed class DirectionMarkCommand : RuntimeCommand<DirectionMarkCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("mark", category: "Direction", usage: "mark <name> at:<mark>")
            .Primary<string>("name")
            .Required<string>("at")
            .Build(static values => new Arguments(
                values.Required<string>("name"),
                values.Required<string>("at")));

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

    public sealed record Arguments(string Name, string At);
}

public sealed class CurtainCommand : RuntimeCommand<CurtainCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create(
                "curtain",
                category: "Direction",
                usage: "curtain <open|closed> [duration:<seconds>] [r:<0..1>] [g:<0..1>] [b:<0..1>] [wait:<bool>]")
            .Primary<CurtainPosition>("state")
            .Optional<double>("duration", contractDefault: 0.35, hasContractDefault: true, minimum: 0)
            .Optional<double>("r", contractDefault: 0.0, hasContractDefault: true, minimum: 0)
            .Optional<double>("g", contractDefault: 0.0, hasContractDefault: true, minimum: 0)
            .Optional<double>("b", contractDefault: 0.0, hasContractDefault: true, minimum: 0)
            .Use("wait", CommandParameterGroups.Wait(contractDefault: false))
            .Build(static values => new Arguments(
                values.Required<CurtainPosition>("state"),
                values.Optional<double>("duration").GetValueOrDefault(0.35),
                values.Optional<double>("r").GetValueOrDefault(0.0),
                values.Optional<double>("g").GetValueOrDefault(0.0),
                values.Optional<double>("b").GetValueOrDefault(0.0),
                values.Group<WaitArguments>("wait")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var transition = new DirectionTransitionSpec(
            DirectionTransitionId.Fade,
            arguments.Duration,
            EasingId.Linear).Validate();
        var opacity = arguments.Position == CurtainPosition.Open ? 0f : 1f;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        await beats.SubmitAsync(
            new CurtainPatch(
                new CurtainState(
                    new DirectionColor((float)arguments.R, (float)arguments.G, (float)arguments.B),
                    opacity),
                transition,
                new DirectionSourceLocation(node, 0, 0)),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(
        CurtainPosition Position,
        double Duration,
        double R,
        double G,
        double B,
        WaitArguments Wait);
}
