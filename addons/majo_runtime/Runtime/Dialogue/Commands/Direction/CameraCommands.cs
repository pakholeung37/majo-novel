#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("camera", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
[AtLeastOne(nameof(Arguments.Center), nameof(Arguments.Zoom), nameof(Arguments.Rotation), nameof(Arguments.Dolly), nameof(Arguments.As), nameof(Arguments.Preset))]
public sealed partial class CameraCommand : RuntimeCommand<CameraCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;
    private readonly CameraEnvironmentPresetCatalog environment;

    public CameraCommand(
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
        var via = arguments.Via.WasProvided ? arguments.Via.Value : (DirectionTransitionId?)null;
        var transition = policy.ResolveCameraTransition("camera", arguments.Timing, via, context);
        var wait = arguments.Timing.Wait.GetValueOrDefault(false);

        var targetView = ResolveTarget(node, arguments.Stage.Value, arguments.View.Value);
        var preset = arguments.Preset.WasProvided ? scopes.ResolveCameraPreset(node, arguments.Preset.Value!) : null;
        var patch = new ViewPosePatch(
            targetView,
            arguments.Center.WasProvided
                ? DirectionValue.From(scopes.ResolvePosition(node, arguments.Center.Value!))
                : preset?.Center ?? default,
            arguments.Zoom.WasProvided
                ? DirectionValue.From((float)arguments.Zoom.Value)
                : preset?.Zoom ?? default,
            arguments.Rotation.WasProvided
                ? DirectionValue.From((float)arguments.Rotation.Value)
                : preset?.RotationDegrees ?? default,
            transition)
        {
            Dolly = arguments.Dolly.WasProvided
                ? DirectionValue.From(environment.ResolveDolly(arguments.Dolly.Value!))
                : preset?.Dolly ?? default,
            Parallax = preset?.Parallax,
        };

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
        [Option] CommandValue<PositionRef> Center,
        [Option(Minimum = 0.001, ValueHint = "number")] CommandValue<double> Zoom,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation,
        [Option(ValueHint = "neutral|near|back|project preset")] CommandValue<string> Dolly,
        [Option(ValueHint = "shot")] CommandValue<string> As,
        [Option(ValueHint = "kind")] CommandValue<DirectionTransitionId> Via,
        [Group] TimingArguments Timing,
        [Option] CommandValue<string> Preset);
}

[Command("camera move", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
[AtLeastOne(nameof(Arguments.Center), nameof(Arguments.Zoom), nameof(Arguments.Rotation), nameof(Arguments.Dolly), nameof(Arguments.Preset))]
public sealed partial class CameraMoveCommand : RuntimeCommand<CameraMoveCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodeAccessor;
    private readonly IDirectionCommandPolicy policy;
    private readonly CameraEnvironmentPresetCatalog environment;

    public CameraMoveCommand(
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
        var transition = policy.ResolveMotionTransition("camera move", arguments.Timing, DirectionTransitionId.Move, context);
        var wait = arguments.Timing.Wait.GetValueOrDefault(false);

        var targetView = ResolveTarget(node, arguments.Stage.Value, arguments.View.Value);
        var preset = arguments.Preset.WasProvided ? scopes.ResolveCameraPreset(node, arguments.Preset.Value!) : null;
        var patch = new ViewPosePatch(
            targetView,
            arguments.Center.WasProvided
                ? DirectionValue.From(scopes.ResolvePosition(node, arguments.Center.Value!))
                : preset?.Center ?? default,
            arguments.Zoom.WasProvided
                ? DirectionValue.From((float)arguments.Zoom.Value)
                : preset?.Zoom ?? default,
            arguments.Rotation.WasProvided
                ? DirectionValue.From((float)arguments.Rotation.Value)
                : preset?.RotationDegrees ?? default,
            transition)
        {
            Dolly = arguments.Dolly.WasProvided
                ? DirectionValue.From(environment.ResolveDolly(arguments.Dolly.Value!))
                : preset?.Dolly ?? default,
            Parallax = preset?.Parallax,
        };

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
        [Option] CommandValue<PositionRef> Center,
        [Option(Minimum = 0.001, ValueHint = "number")] CommandValue<double> Zoom,
        [Option(ValueHint = "degrees")] CommandValue<double> Rotation,
        [Option(ValueHint = "neutral|near|back|project preset")] CommandValue<string> Dolly,
        [Group] TimingArguments Timing,
        [Option] CommandValue<string> Preset);
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
            transition)
        {
            Dolly = DirectionValue.From(0f),
        };

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
