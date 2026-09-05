#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record DirectionAnimationDefaults(
    double DefaultAnimationDurationSeconds,
    double DefaultCharacterAnimationDurationSeconds,
    DirectionTransitionKind DefaultCharacterAnimationKind,
    DirectionTransitionKind DefaultLeaveAnimationKind = DirectionTransitionKind.Fade,
    DirectionTransitionKind DefaultStageShowAnimationKind = DirectionTransitionKind.Cut,
    DirectionTransitionKind DefaultStageClearAnimationKind = DirectionTransitionKind.Fade,
    DirectionTransitionKind DefaultBackdropAnimationKind = DirectionTransitionKind.Cut,
    DirectionTransitionKind DefaultCameraPoseAnimationKind = DirectionTransitionKind.Cut,
    DirectionTransitionKind DefaultCameraResetAnimationKind = DirectionTransitionKind.Cut,
    DirectionTransitionKind DefaultShotAnimationKind = DirectionTransitionKind.Cut,
    DirectionTransitionKind DefaultLayoutAnimationKind = DirectionTransitionKind.Cut,
    double DefaultCurtainDurationSeconds = RuntimeDefaults.CurtainDurationSeconds)
{
    public static DirectionAnimationDefaults FromConfiguration(AnimationConfiguration? animation)
    {
        if (animation is null)
        {
            return new DirectionAnimationDefaults(
                RuntimeDefaults.AnimationDurationSeconds,
                RuntimeDefaults.CommandBindingCharacterAnimationDurationSeconds,
                DirectionTransitionKind.Fade);
        }

        return new DirectionAnimationDefaults(
            animation.DefaultAnimationDurationSeconds,
            animation.DefaultCharacterAnimationDurationSeconds,
            animation.DefaultCharacterAnimationKind,
            animation.DefaultLeaveAnimationKind,
            animation.DefaultStageShowAnimationKind,
            animation.DefaultStageClearAnimationKind,
            animation.DefaultBackdropAnimationKind,
            animation.DefaultCameraPoseAnimationKind,
            animation.DefaultCameraResetAnimationKind,
            animation.DefaultShotAnimationKind,
            animation.DefaultLayoutAnimationKind,
            animation.DefaultCurtainDurationSeconds);
    }

    public DirectionTransitionKind ResolveDefaultAnimationKind(string commandName) => commandName switch
    {
        "leave" => DefaultLeaveAnimationKind,
        "stage show" => DefaultStageShowAnimationKind,
        "stage clear" => DefaultStageClearAnimationKind,
        "backdrop" => DefaultBackdropAnimationKind,
        "camera pose" => DefaultCameraPoseAnimationKind,
        "camera reset" => DefaultCameraResetAnimationKind,
        "shot use" => DefaultShotAnimationKind,
        "layout" => DefaultLayoutAnimationKind,
        "character" => DefaultCharacterAnimationKind,
        _ => DefaultCharacterAnimationKind,
    };
}

public interface IDirectionCommandPolicy : IRuntimeService
{
    DirectionTransitionSpec ResolveTransition(
        string commandName,
        TransitionArguments input,
        CommandExecutionContext context);

    DirectionTransitionSpec ResolveMotionTransition(
        string commandName,
        TimingArguments input,
        DirectionTransitionId kind,
        CommandExecutionContext context);

    DirectionTransitionSpec ResolveCameraPoseTransition(
        string commandName,
        TimingArguments input,
        DirectionTransitionId? via,
        CommandExecutionContext context,
        bool reset = false);
}

public sealed class DirectionCommandPolicy : IDirectionCommandPolicy
{
    private readonly IDirectionBeatRuntime beats;
    private readonly DirectionAnimationDefaults defaults;
    private readonly ITransitionDriverRegistry? transitions;

    public DirectionCommandPolicy(
        IDirectionBeatRuntime? beats = null,
        DirectionAnimationDefaults? defaults = null,
        ITransitionDriverRegistry? transitions = null)
    {
        this.beats = beats ?? new NullDirectionBeatRuntime();
        this.defaults = defaults ?? DirectionAnimationDefaults.FromConfiguration(null);
        this.transitions = transitions;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public DirectionTransitionSpec ResolveTransition(
        string commandName,
        TransitionArguments input,
        CommandExecutionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(input);

        var isCharacterCommand = commandName is "character" or "leave";
        var kind = input.Via.WasProvided
            ? input.Via.Value
            : DirectionTransitionId.FromKind(defaults.ResolveDefaultAnimationKind(commandName));

        var isRegistered = !isCharacterCommand && transitions is not null &&
            transitions.Supports(kind, TransitionDomains.Stage);

        if (!kind.TryToKind(out _) && !isRegistered)
        {
            throw new DirectionException(
                $"Transition '{kind.ToString().ToLowerInvariant()}' is not supported by this command.");
        }

        var openBeatDefaults = beats.OpenBeatDefaults;
        double duration;
        if (input.Duration.WasProvided)
        {
            duration = input.Duration.Value;
        }
        else if (kind == DirectionTransitionId.Cut)
        {
            duration = 0;
        }
        else if (isCharacterCommand)
        {
            duration = defaults.DefaultCharacterAnimationDurationSeconds != 0
                ? defaults.DefaultCharacterAnimationDurationSeconds
                : openBeatDefaults?.DurationSeconds ?? 0;
        }
        else
        {
            duration = openBeatDefaults?.DurationSeconds ?? defaults.DefaultAnimationDurationSeconds;
        }

        var ease = input.Ease.WasProvided
            ? input.Ease.Value
            : openBeatDefaults?.Ease ?? DirectionEase.Linear;

        return new DirectionTransitionSpec(kind, duration, ease).Validate();
    }

    public DirectionTransitionSpec ResolveMotionTransition(
        string commandName,
        TimingArguments input,
        DirectionTransitionId kind,
        CommandExecutionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(input);

        var openBeatDefaults = beats.OpenBeatDefaults;
        var duration = input.Duration.WasProvided
            ? input.Duration.Value
            : openBeatDefaults?.DurationSeconds ?? defaults.DefaultAnimationDurationSeconds;

        var ease = input.Ease.WasProvided
            ? input.Ease.Value
            : openBeatDefaults?.Ease ?? DirectionEase.Linear;

        return new DirectionTransitionSpec(kind, duration, ease).Validate();
    }

    public DirectionTransitionSpec ResolveCameraPoseTransition(
        string commandName,
        TimingArguments input,
        DirectionTransitionId? via,
        CommandExecutionContext context,
        bool reset = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(input);

        // A pose command with an explicit duration but no `via` is the common
        // camera-pan shorthand. The cut remains the default when no duration
        // is supplied, while `via:cut` still requires duration:0.
        DirectionTransitionId kind;
        if (via.HasValue && !via.Value.IsEmpty)
        {
            kind = via.Value;
        }
        else if (input.Duration.WasProvided)
        {
            kind = DirectionTransitionId.Move;
        }
        else
        {
            kind = DirectionTransitionId.FromKind(defaults.ResolveDefaultAnimationKind(reset ? "camera reset" : "camera pose"));
        }

        var isRegistered = transitions is not null &&
            transitions.Supports(kind, TransitionDomains.View);

        if (!kind.TryToKind(out _) && !isRegistered)
        {
            throw new DirectionException(
                $"Transition '{kind.ToString().ToLowerInvariant()}' is not supported by this command.");
        }

        var openBeatDefaults = beats.OpenBeatDefaults;
        double duration;
        if (input.Duration.WasProvided)
        {
            duration = input.Duration.Value;
        }
        else if (kind == DirectionTransitionId.Cut)
        {
            duration = 0;
        }
        else
        {
            duration = openBeatDefaults?.DurationSeconds ?? defaults.DefaultAnimationDurationSeconds;
        }

        var ease = input.Ease.WasProvided
            ? input.Ease.Value
            : openBeatDefaults?.Ease ?? DirectionEase.Linear;

        return new DirectionTransitionSpec(kind, duration, ease).Validate();
    }

    private sealed class NullDirectionBeatRuntime : IDirectionBeatRuntime
    {
        public bool HasOpenBeat => false;
        public string? OpenBeatId => null;
        public DirectionBeatDefaults? OpenBeatDefaults => null;
        public void Begin(string beatId, string nodeId, DirectionBeatDefaults? defaults = null) { }
        public Task<CommittedDirectionTransaction?> SubmitAsync(IDirectionDirective directive, DirectionCompletionBarrier completion = DirectionCompletionBarrier.Commit, CancellationToken cancellationToken = default) => Task.FromResult<CommittedDirectionTransaction?>(null);
        public Task<CommittedDirectionTransaction> EndAsync(DirectionCompletionBarrier completion = DirectionCompletionBarrier.Settle, CancellationToken cancellationToken = default) => Task.FromResult<CommittedDirectionTransaction>(null!);
        public void AbortOpen() { }
        public Task JoinAsync(string beatId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FinishAsync(string beatId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void ValidateNodeBoundary(string nodeId) { }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
