#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class ViewDefineCommand : RuntimeCommand<ViewDefineCommand.Arguments>
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

    public override CommandSchema<Arguments> Schema { get; } =
        CommandSchema<Arguments>
            .Create("view define", category: "Direction", usage: "view define <id> stage:<id>")
            .Primary<string>("view")
            .Required<string>("stage")
            .Build(static values => new Arguments(
                values.Required<string>("view"),
                values.Required<string>("stage")));

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

    public sealed record Arguments(string View, string Stage);
}

public sealed class ViewReleaseCommand : RuntimeCommand<ViewReleaseCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;

    public ViewReleaseCommand(
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
            .Create("view release", category: "Direction", usage: "view release <id>")
            .Primary<string>("view")
            .Build(static values => new Arguments(values.Required<string>("view")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var viewId = scopes.ResolveView(nodeAccessor.RequireNode(), arguments.View);
        await beats.SubmitAsync(
            new ViewReleasePatch(viewId, DirectionTransitionSpec.Cut),
            DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    public sealed record Arguments(string View);
}

public sealed class CameraPoseCommand : RuntimeCommand<CameraPoseCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public CameraPoseCommand(
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
                "camera pose",
                category: "Direction",
                usage: "camera pose [stage:<id>|view:<id>] [center:<x,y>] [zoom:<number>] [rotation:<degrees>] [as:<shot>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Optional<string>("stage")
            .Optional<string>("view")
            .Optional<Vector2>("center")
            .Optional<double>("zoom", minimum: 0.001)
            .Optional<double>("rotation")
            .Optional<string>("as")
            .Optional<DirectionTransitionId>("via")
            .Use("timing", CommandParameterGroups.Timing())
            .ParameterSet(CommandParameterSetRule.AtMostOne, "stage", "view")
            .ParameterSet(CommandParameterSetRule.AtLeastOne, "center", "zoom", "rotation", "as")
            .Build(static values => new Arguments(
                values.Optional<string>("stage"),
                values.Optional<string>("view"),
                values.Optional<Vector2>("center"),
                values.Optional<double>("zoom"),
                values.Optional<double>("rotation"),
                values.Optional<string>("as"),
                values.Optional<DirectionTransitionId>("via"),
                values.Group<TimingArguments>("timing")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var via = arguments.Via.WasProvided ? arguments.Via.Value : (DirectionTransitionId?)null;
        var transition = policy.ResolveCameraPoseTransition("camera pose", arguments.Timing, via, context);
        var wait = arguments.Timing.Wait.GetValueOrDefault(false);

        var targetView = ResolveTarget(node, arguments.Stage.Value, arguments.View.Value);
        var patch = new ViewPosePatch(
            targetView,
            arguments.Center.WasProvided
                ? DirectionValue.From(new DirectionPoint(arguments.Center.Value.X, arguments.Center.Value.Y))
                : default,
            arguments.Zoom.WasProvided
                ? DirectionValue.From((float)arguments.Zoom.Value)
                : default,
            arguments.Rotation.WasProvided
                ? DirectionValue.From((float)arguments.Rotation.Value)
                : default,
            transition);

        if (arguments.As.WasProvided && !string.IsNullOrWhiteSpace(arguments.As.Value))
        {
            scopes.DefineShot(node, arguments.As.Value, patch);
        }

        await beats.SubmitAsync(
            patch,
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    private ViewId ResolveTarget(string node, string? stage, string? view) =>
        !string.IsNullOrWhiteSpace(stage)
            ? scopes.ResolveDefaultView(node, stage)
            : scopes.ResolveView(node, string.IsNullOrWhiteSpace(view) ? "main" : view);

    public sealed record Arguments(
        CommandValue<string> Stage,
        CommandValue<string> View,
        CommandValue<Vector2> Center,
        CommandValue<double> Zoom,
        CommandValue<double> Rotation,
        CommandValue<string> As,
        CommandValue<DirectionTransitionId> Via,
        TimingArguments Timing);
}

public sealed class CameraMoveCommand : RuntimeCommand<CameraMoveCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public CameraMoveCommand(
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
                "camera move",
                category: "Direction",
                usage: "camera move [stage:<id>|view:<id>] [center:<x,y>] [zoom:<number>] [rotation:<degrees>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Optional<string>("stage")
            .Optional<string>("view")
            .Optional<Vector2>("center")
            .Optional<double>("zoom", minimum: 0.001)
            .Optional<double>("rotation")
            .Use("timing", CommandParameterGroups.Timing())
            .ParameterSet(CommandParameterSetRule.AtMostOne, "stage", "view")
            .ParameterSet(CommandParameterSetRule.AtLeastOne, "center", "zoom", "rotation")
            .Build(static values => new Arguments(
                values.Optional<string>("stage"),
                values.Optional<string>("view"),
                values.Optional<Vector2>("center"),
                values.Optional<double>("zoom"),
                values.Optional<double>("rotation"),
                values.Group<TimingArguments>("timing")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var transition = policy.ResolveMotionTransition("camera move", arguments.Timing, DirectionTransitionKind.Move, context);
        var wait = arguments.Timing.Wait.GetValueOrDefault(false);

        var targetView = ResolveTarget(node, arguments.Stage.Value, arguments.View.Value);
        var patch = new ViewPosePatch(
            targetView,
            arguments.Center.WasProvided
                ? DirectionValue.From(new DirectionPoint(arguments.Center.Value.X, arguments.Center.Value.Y))
                : default,
            arguments.Zoom.WasProvided
                ? DirectionValue.From((float)arguments.Zoom.Value)
                : default,
            arguments.Rotation.WasProvided
                ? DirectionValue.From((float)arguments.Rotation.Value)
                : default,
            transition);

        await beats.SubmitAsync(
            patch,
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    private ViewId ResolveTarget(string node, string? stage, string? view) =>
        !string.IsNullOrWhiteSpace(stage)
            ? scopes.ResolveDefaultView(node, stage)
            : scopes.ResolveView(node, string.IsNullOrWhiteSpace(view) ? "main" : view);

    public sealed record Arguments(
        CommandValue<string> Stage,
        CommandValue<string> View,
        CommandValue<Vector2> Center,
        CommandValue<double> Zoom,
        CommandValue<double> Rotation,
        TimingArguments Timing);
}

public sealed class CameraResetCommand : RuntimeCommand<CameraResetCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public CameraResetCommand(
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
                "camera reset",
                category: "Direction",
                usage: "camera reset [stage:<id>|view:<id>] [via:<kind>] [duration:<seconds>] [ease:<curve>] [wait:<bool>]")
            .Optional<string>("stage")
            .Optional<string>("view")
            .Use("transition", CommandParameterGroups.Transition())
            .ParameterSet(CommandParameterSetRule.AtMostOne, "stage", "view")
            .Build(static values => new Arguments(
                values.Optional<string>("stage"),
                values.Optional<string>("view"),
                values.Group<TransitionArguments>("transition")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var via = arguments.Transition.Via.WasProvided ? arguments.Transition.Via.Value : (DirectionTransitionId?)null;
        var timing = new TimingArguments(arguments.Transition.Duration, arguments.Transition.Ease, arguments.Transition.Wait);
        var transition = policy.ResolveCameraPoseTransition("camera reset", timing, via, context, reset: true);
        var wait = arguments.Transition.Wait.GetValueOrDefault(false);

        var targetView = ResolveTarget(node, arguments.Stage.Value, arguments.View.Value);
        var patch = new ViewPosePatch(
            targetView,
            DirectionValue.From(new DirectionPoint(50, 50)),
            DirectionValue.From(1f),
            DirectionValue.From(0f),
            transition);

        await beats.SubmitAsync(
            patch,
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    private ViewId ResolveTarget(string node, string? stage, string? view) =>
        !string.IsNullOrWhiteSpace(stage)
            ? scopes.ResolveDefaultView(node, stage)
            : scopes.ResolveView(node, string.IsNullOrWhiteSpace(view) ? "main" : view);

    public sealed record Arguments(
        CommandValue<string> Stage,
        CommandValue<string> View,
        TransitionArguments Transition);
}

public sealed class CameraFxCommand : RuntimeCommand<CameraFxCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;

    public CameraFxCommand(
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
            .Create(
                "camera fx",
                category: "Direction",
                usage: "camera fx <effect> [stage:<id>|view:<id>] [duration:<seconds>] [strength:<number>] [axis:<all|x|y|diagonal>] [frequency:<number>] [zoom:<number>] [wait:<bool>]")
            .Primary<string>("effect")
            .Optional<string>("stage")
            .Optional<string>("view")
            .Optional<double>("duration", minimum: 0)
            .Optional<double>("strength", minimum: 0)
            .Optional<CameraShakeAxis>("axis")
            .Optional<int>("frequency")
            .Optional<double>("zoom")
            .Use("wait", CommandParameterGroups.Wait(contractDefault: false))
            .ParameterSet(CommandParameterSetRule.AtMostOne, "stage", "view")
            .Build(static values => new Arguments(
                values.Required<string>("effect"),
                values.Optional<string>("stage"),
                values.Optional<string>("view"),
                values.Optional<double>("duration"),
                values.Optional<double>("strength"),
                values.Optional<CameraShakeAxis>("axis"),
                values.Optional<int>("frequency"),
                values.Optional<double>("zoom"),
                values.Group<WaitArguments>("wait")));

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments.Strength.WasProvided)
            parameters["strength"] = arguments.Strength.Value.ToString(CultureInfo.InvariantCulture);
        if (arguments.Axis.WasProvided)
            parameters["axis"] = arguments.Axis.Value.ToString().ToLowerInvariant();
        if (arguments.Frequency.WasProvided)
            parameters["frequency"] = arguments.Frequency.Value.ToString(CultureInfo.InvariantCulture);
        if (arguments.Zoom.WasProvided)
            parameters["zoom"] = arguments.Zoom.Value.ToString(CultureInfo.InvariantCulture);

        var duration = arguments.Duration.WasProvided
            ? arguments.Duration.Value
            : 0.4;
        var wait = arguments.Wait.Wait.GetValueOrDefault(false);

        var targetView = ResolveTarget(node, arguments.Stage.Value, arguments.View.Value);

        await beats.SubmitAsync(
            new CameraFxCue(
                targetView,
                arguments.Effect,
                parameters,
                duration,
                new DirectionSourceLocation(node, 0, 0)),
            wait ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    private ViewId ResolveTarget(string node, string? stage, string? view) =>
        !string.IsNullOrWhiteSpace(stage)
            ? scopes.ResolveDefaultView(node, stage)
            : scopes.ResolveView(node, string.IsNullOrWhiteSpace(view) ? "main" : view);

    public sealed record Arguments(
        string Effect,
        CommandValue<string> Stage,
        CommandValue<string> View,
        CommandValue<double> Duration,
        CommandValue<double> Strength,
        CommandValue<CameraShakeAxis> Axis,
        CommandValue<int> Frequency,
        CommandValue<double> Zoom,
        WaitArguments Wait);
}
