#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Majo.Runtime.Configuration;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Execution;
using Majo.Runtime.Direction.Transitions;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record DirectionAnimationDefaults(
    double DefaultAnimationDurationSeconds,
    double DefaultCharacterAnimationDurationSeconds,
    DirectionTransitionId DefaultCharacterAnimationKind,
    DirectionTransitionId DefaultLeaveAnimationKind = default,
    DirectionTransitionId DefaultStageShowAnimationKind = default,
    DirectionTransitionId DefaultStageClearAnimationKind = default,
    DirectionTransitionId DefaultBackdropAnimationKind = default,
    DirectionTransitionId DefaultCameraPoseAnimationKind = default,
    DirectionTransitionId DefaultCameraResetAnimationKind = default,
    DirectionTransitionId DefaultShotAnimationKind = default,
    DirectionTransitionId DefaultLayoutAnimationKind = default,
    double DefaultCurtainDurationSeconds = RuntimeDefaults.CurtainDurationSeconds,
    EasingId DefaultEasing = default)
{
    public static DirectionAnimationDefaults FromConfiguration(AnimationConfiguration? animation)
    {
        if (animation is null)
        {
            return new DirectionAnimationDefaults(
                RuntimeDefaults.AnimationDurationSeconds,
                RuntimeDefaults.CommandBindingCharacterAnimationDurationSeconds,
                DirectionTransitionId.Fade);
        }

        return new DirectionAnimationDefaults(
            animation.DefaultAnimationDurationSeconds,
            animation.DefaultCharacterAnimationDurationSeconds,
            animation.ResolveCharacterAnimationId(),
            animation.ResolveLeaveAnimationId(),
            animation.ResolveStageShowAnimationId(),
            animation.ResolveStageClearAnimationId(),
            animation.ResolveBackdropAnimationId(),
            animation.ResolveCameraPoseAnimationId(),
            animation.ResolveCameraResetAnimationId(),
            animation.ResolveShotAnimationId(),
            animation.ResolveLayoutAnimationId(),
            animation.DefaultCurtainDurationSeconds,
            animation.ResolveDirectionEasingId());
    }

    public DirectionTransitionId ResolveDefaultAnimationKind(string commandName) => commandName switch
    {
        "leave" => DefaultLeaveAnimationKind.IsEmpty ? DirectionTransitionId.Fade : DefaultLeaveAnimationKind,
        "stage show" => DefaultStageShowAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultStageShowAnimationKind,
        "stage clear" => DefaultStageClearAnimationKind.IsEmpty ? DirectionTransitionId.Fade : DefaultStageClearAnimationKind,
        "backdrop" => DefaultBackdropAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultBackdropAnimationKind,
        "camera pose" => DefaultCameraPoseAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultCameraPoseAnimationKind,
        "camera reset" => DefaultCameraResetAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultCameraResetAnimationKind,
        "shot use" => DefaultShotAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultShotAnimationKind,
        "layout" => DefaultLayoutAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultLayoutAnimationKind,
        "character" => DefaultCharacterAnimationKind.IsEmpty ? DirectionTransitionId.Fade : DefaultCharacterAnimationKind,
        _ => DefaultCharacterAnimationKind.IsEmpty ? DirectionTransitionId.Fade : DefaultCharacterAnimationKind,
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
    private readonly IEasingRegistry easings;

    public DirectionCommandPolicy(
        IDirectionBeatRuntime? beats = null,
        DirectionAnimationDefaults? defaults = null,
        ITransitionDriverRegistry? transitions = null,
        IEasingRegistry? easings = null)
    {
        this.beats = beats ?? new NullDirectionBeatRuntime();
        this.defaults = defaults ?? DirectionAnimationDefaults.FromConfiguration(null);
        this.transitions = transitions;
        this.easings = easings ?? CreateDefaultEasings();
    }

    private static EasingRegistry CreateDefaultEasings()
    {
        var registry = new EasingRegistry();
        BuiltInEasings.RegisterDefaults(registry);
        registry.Seal();
        return registry;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        easings.GetRequired(defaults.DefaultEasing.IsEmpty ? EasingId.Linear : defaults.DefaultEasing);
        if (transitions is not null)
        {
            ValidateDefaultTransition(defaults.DefaultCharacterAnimationKind, "character", TransitionDomains.Actor);
            ValidateDefaultTransition(defaults.DefaultLeaveAnimationKind, "leave", TransitionDomains.Actor);
            ValidateDefaultTransition(defaults.DefaultStageShowAnimationKind, "stage show", TransitionDomains.Stage);
            ValidateDefaultTransition(defaults.DefaultStageClearAnimationKind, "stage clear", TransitionDomains.Stage);
            ValidateDefaultTransition(defaults.DefaultBackdropAnimationKind, "backdrop", TransitionDomains.Stage);
            ValidateDefaultTransition(defaults.DefaultCameraPoseAnimationKind, "camera pose", TransitionDomains.View);
            ValidateDefaultTransition(defaults.DefaultCameraResetAnimationKind, "camera reset", TransitionDomains.View);
            ValidateDefaultTransition(defaults.DefaultShotAnimationKind, "shot use", TransitionDomains.View);
            ValidateDefaultTransition(defaults.DefaultLayoutAnimationKind, "layout", TransitionDomains.Screen);
        }
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static TransitionDomains ResolveCommandDomain(string commandName) => commandName switch
    {
        "character" or "leave" => TransitionDomains.Actor,
        "camera pose" or "camera reset" or "camera move" or "camera fx" or "shot use" => TransitionDomains.View,
        "curtain" or "layout" => TransitionDomains.Screen,
        _ => TransitionDomains.Stage,
    };

    private static bool IsBuiltInTransition(DirectionTransitionId kind) =>
        kind == DirectionTransitionId.Cut ||
        kind == DirectionTransitionId.Move ||
        kind == DirectionTransitionId.Fade ||
        kind == DirectionTransitionId.Dissolve;

    private void ValidateDefaultTransition(DirectionTransitionId id, string commandName, TransitionDomains domain)
    {
        if (id.IsEmpty) return;
        var supported = (domain is TransitionDomains.View or TransitionDomains.Actor or TransitionDomains.Screen && id == DirectionTransitionId.Move)
            || transitions!.Supports(id, domain);
        if (!supported)
        {
            throw new DirectionException(
                $"Default transition '{id}' configured for command '{commandName}' is not registered or does not support domain '{domain}'.");
        }
    }

    public DirectionTransitionSpec ResolveTransition(
        string commandName,
        TransitionArguments input,
        CommandExecutionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(input);

        var domain = ResolveCommandDomain(commandName);
        var isCharacterCommand = domain == TransitionDomains.Actor;
        var kind = input.Via.WasProvided
            ? input.Via.Value
            : defaults.ResolveDefaultAnimationKind(commandName);

        var isSupported = transitions is not null
            ? ((domain is TransitionDomains.View or TransitionDomains.Actor or TransitionDomains.Screen && kind == DirectionTransitionId.Move) || transitions.Supports(kind, domain))
            : IsBuiltInTransition(kind);

        if (!isSupported)
        {
            throw new DirectionException(
                $"Transition '{kind.ToString().ToLowerInvariant()}' is not supported by command '{commandName}' (domain '{domain}').");
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

        var ease = (input.Ease.WasProvided && !input.Ease.Value.IsEmpty)
            ? input.Ease.Value
            : (openBeatDefaults?.Easing is { IsEmpty: false } beatEase)
                ? beatEase
                : defaults.DefaultEasing.IsEmpty ? EasingId.Linear : defaults.DefaultEasing;

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

        var ease = (input.Ease.WasProvided && !input.Ease.Value.IsEmpty)
            ? input.Ease.Value
            : (openBeatDefaults?.Easing is { IsEmpty: false } beatEase)
                ? beatEase
                : defaults.DefaultEasing.IsEmpty ? EasingId.Linear : defaults.DefaultEasing;

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
            kind = defaults.ResolveDefaultAnimationKind(reset ? "camera reset" : "camera pose");
        }

        var isSupported = transitions is not null
            ? (kind == DirectionTransitionId.Move || transitions.Supports(kind, TransitionDomains.View))
            : IsBuiltInTransition(kind);

        if (!isSupported)
        {
            throw new DirectionException(
                $"Transition '{kind.ToString().ToLowerInvariant()}' is not supported by command '{commandName}' (domain 'View').");
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

        var ease = (input.Ease.WasProvided && !input.Ease.Value.IsEmpty)
            ? input.Ease.Value
            : (openBeatDefaults?.Easing is { IsEmpty: false } beatEase)
                ? beatEase
                : defaults.DefaultEasing.IsEmpty ? EasingId.Linear : defaults.DefaultEasing;

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
