#nullable enable

using System;
using System.Threading.Tasks;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;

namespace Majo.Runtime.Dialogue.Commands;

[Command("camera parallax", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
public sealed partial class CameraParallaxCommand : RuntimeCommand<CameraParallaxCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    private readonly IDirectionCommandPolicy policy;
    private readonly CameraEnvironmentPresetCatalog presets;

    public CameraParallaxCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodes, IDirectionCommandPolicy policy,
        CameraEnvironmentPresetCatalog? presets = null)
    {
        this.beats = beats; this.scopes = scopes; this.nodes = nodes; this.policy = policy;
        this.presets = presets ?? new CameraEnvironmentPresetCatalog();
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodes.RequireNode();
        var view = ResolveTarget(node, arguments.Stage.Value, arguments.View.Value);
        var name = arguments.Preset.Equals("reset", StringComparison.OrdinalIgnoreCase) ? "off" : arguments.Preset;
        var transition = ResolveTransition("camera parallax", arguments.Timing, context);
        await beats.SubmitAsync(new ViewParallaxPatch(view, presets.ResolveParallax(name), transition),
            arguments.Timing.Wait.GetValueOrDefault(false) ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    private DirectionTransitionSpec ResolveTransition(string command, TimingArguments timing, CommandExecutionContext context)
    {
        var resolved = policy.ResolveMotionTransition(command, timing, DirectionTransitionId.Move, context);
        return timing.Duration.GetValueOrDefault(0) == 0
            ? DirectionTransitionSpec.Cut
            : resolved;
    }

    private ViewId ResolveTarget(string node, string? stage, string? view) =>
        !string.IsNullOrWhiteSpace(stage) ? scopes.ResolveDefaultView(node, stage) :
        scopes.ResolveView(node, string.IsNullOrWhiteSpace(view) ? "main" : view);

    public sealed record Arguments(
        [Primary] string Preset,
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Group] TimingArguments Timing);
}

[Command("camera idle", Category = "Direction")]
[AtMostOne(nameof(Arguments.Stage), nameof(Arguments.View))]
public sealed partial class CameraIdleCommand : RuntimeCommand<CameraIdleCommand.Arguments>
{
    private readonly IDirectionBeatRuntime beats;
    private readonly IDirectionScopeResolver scopes;
    private readonly IDialogueNodeAccessor nodes;
    private readonly IDirectionCommandPolicy policy;
    private readonly CameraEnvironmentPresetCatalog presets;

    public CameraIdleCommand(IDirectionBeatRuntime beats, IDirectionScopeResolver scopes,
        IDialogueNodeAccessor nodes, IDirectionCommandPolicy policy,
        CameraEnvironmentPresetCatalog? presets = null)
    {
        this.beats = beats; this.scopes = scopes; this.nodes = nodes; this.policy = policy;
        this.presets = presets ?? new CameraEnvironmentPresetCatalog();
    }

    protected override async ValueTask ExecuteAsync(Arguments arguments, CommandExecutionContext context)
    {
        var node = nodes.RequireNode();
        var view = ResolveTarget(node, arguments.Stage.Value, arguments.View.Value);
        var timing = arguments.Timing.Duration.HasValue ? arguments.Timing : arguments.Timing with
        {
            Duration = CommandValue<double>.Explicit(0.5),
        };
        var resolved = policy.ResolveMotionTransition("camera idle", timing, DirectionTransitionId.Move, context);
        var transition = timing.Duration.GetValueOrDefault(0.5) == 0
            ? DirectionTransitionSpec.Cut : resolved;
        await beats.SubmitAsync(new ViewIdlePatch(view, presets.ResolveIdle(arguments.Preset), transition),
            arguments.Timing.Wait.GetValueOrDefault(false) ? DirectionCompletionBarrier.Settle : DirectionCompletionBarrier.Commit,
            context.CancellationToken);
    }

    private ViewId ResolveTarget(string node, string? stage, string? view) =>
        !string.IsNullOrWhiteSpace(stage) ? scopes.ResolveDefaultView(node, stage) :
        scopes.ResolveView(node, string.IsNullOrWhiteSpace(view) ? "main" : view);

    public sealed record Arguments(
        [Primary] string Preset,
        [Option] CommandValue<string> Stage,
        [Option] CommandValue<string> View,
        [Group] TimingArguments Timing);
}
