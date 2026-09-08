#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Camera;

namespace Majo.Runtime.Dialogue.Commands;

[Command("camera", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
[AtLeastOne(nameof(Arguments.Center), nameof(Arguments.Zoom), nameof(Arguments.Rotation), nameof(Arguments.As))]
public sealed partial class CameraCommand : RuntimeCommand<CameraCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;

    public CameraCommand(
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
        var via = arguments.Via.WasProvided ? arguments.Via.Value : (DirectionTransitionId?)null;
        var transition = policy.ResolveCameraTransition("camera", arguments.Timing, via, context);
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
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Option(ValueHint = "x,y")] CommandValue<Vector2> Center,
        [Option(Minimum = 0.001, ValueHint = "number")] CommandValue<double> Zoom,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation,
        [Option(ValueHint = "shot")] CommandValue<string> As,
        [Option(ValueHint = "kind")] CommandValue<DirectionTransitionId> Via,
        [Group] TimingArguments Timing);
}

[Command("camera move", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
[AtLeastOne(nameof(Arguments.Center), nameof(Arguments.Zoom), nameof(Arguments.Rotation))]
public sealed partial class CameraMoveCommand : RuntimeCommand<CameraMoveCommand.Arguments>
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

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var transition = policy.ResolveMotionTransition("camera move", arguments.Timing, DirectionTransitionId.Move, context);
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
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Option(ValueHint = "x,y")] CommandValue<Vector2> Center,
        [Option(Minimum = 0.001, ValueHint = "number")] CommandValue<double> Zoom,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation,
        [Group] TimingArguments Timing);
}

[Command("camera reset", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
public sealed partial class CameraResetCommand : RuntimeCommand<CameraResetCommand.Arguments>
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

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodeAccessor.RequireNode();
        var via = arguments.Transition.Via.WasProvided ? arguments.Transition.Via.Value : (DirectionTransitionId?)null;
        var timing = new TimingArguments(arguments.Transition.Duration, arguments.Transition.Ease, arguments.Transition.Wait);
        var transition = policy.ResolveCameraTransition("camera reset", timing, via, context, reset: true);
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
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Group] TransitionArguments Transition);
}

[Command("camera fx", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
public sealed partial class CameraFxCommand : RuntimeCommand<CameraFxCommand.Arguments>
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
            : beats.OpenBeatDefaults?.DurationSeconds ?? 0.4;
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
        [Primary] string Effect,
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Option(Minimum = 0, ValueHint = "seconds")] CommandValue<double> Duration,
        [Option(Minimum = 0, ValueHint = "View viewport pixels")] CommandValue<double> Strength,
        [Option(Converter = typeof(CameraShakeAxisConverter), ValueHint = "all|x|y|diagonal")] CommandValue<CameraShakeAxis> Axis,
        [Option(ValueHint = "number")] CommandValue<int> Frequency,
        [Option(ValueHint = "number")] CommandValue<double> Zoom,
        [Group] WaitArguments Wait);
}
