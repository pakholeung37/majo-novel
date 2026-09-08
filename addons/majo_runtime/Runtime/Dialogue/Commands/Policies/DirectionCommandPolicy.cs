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
    DirectionTransitionId DefaultCharacterAppearanceAnimationKind = default,
    DirectionTransitionId DefaultActorHideAnimationKind = default,
    DirectionTransitionId DefaultStageShowAnimationKind = default,
    DirectionTransitionId DefaultStageClearAnimationKind = default,
    DirectionTransitionId DefaultBackdropAnimationKind = default,
    DirectionTransitionId DefaultCameraAnimationKind = default,
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
                DirectionTransitionId.Fade,
                DirectionTransitionId.Dissolve);
        }

        return new DirectionAnimationDefaults(
            animation.DefaultAnimationDurationSeconds,
            animation.DefaultCharacterAnimationDurationSeconds,
            animation.ResolveCharacterAnimationId(),
            animation.ResolveCharacterAppearanceAnimationId(),
            animation.ResolveActorHideAnimationId(),
            animation.ResolveStageShowAnimationId(),
            animation.ResolveStageClearAnimationId(),
            animation.ResolveBackdropAnimationId(),
            animation.ResolveCameraAnimationId(),
            animation.ResolveCameraResetAnimationId(),
            animation.ResolveShotAnimationId(),
            animation.ResolveLayoutAnimationId(),
            animation.DefaultCurtainDurationSeconds,
            animation.ResolveDirectionEasingId());
    }

    public DirectionTransitionId ResolveDefaultAnimationKind(string commandName) => commandName switch
    {
        "character hide" => DefaultActorHideAnimationKind.IsEmpty ? DirectionTransitionId.Fade : DefaultActorHideAnimationKind,
        "stage show" => DefaultStageShowAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultStageShowAnimationKind,
        "stage clear" => DefaultStageClearAnimationKind.IsEmpty ? DirectionTransitionId.Fade : DefaultStageClearAnimationKind,
        "backdrop" => DefaultBackdropAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultBackdropAnimationKind,
        "camera" => DefaultCameraAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultCameraAnimationKind,
        "camera reset" => DefaultCameraResetAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultCameraResetAnimationKind,
        "shot use" => DefaultShotAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultShotAnimationKind,
        "layout" => DefaultLayoutAnimationKind.IsEmpty ? DirectionTransitionId.Cut : DefaultLayoutAnimationKind,
        "character space" or "object space" => DirectionTransitionId.Cut,
        "object hide" => DefaultActorHideAnimationKind.IsEmpty ? DirectionTransitionId.Fade : DefaultActorHideAnimationKind,
        "character appearance" or "object appearance" => DefaultCharacterAppearanceAnimationKind.IsEmpty ? DirectionTransitionId.Dissolve : DefaultCharacterAppearanceAnimationKind,
        "character" or "object" => DefaultCharacterAnimationKind.IsEmpty ? DirectionTransitionId.Fade : DefaultCharacterAnimationKind,
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

    DirectionTransitionSpec ResolveCameraTransition(
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
            ValidateDefaultTransition(defaults.DefaultCharacterAppearanceAnimationKind, "character appearance", TransitionDomains.Actor);
            ValidateDefaultTransition(defaults.DefaultActorHideAnimationKind, "character hide", TransitionDomains.Actor);
            ValidateDefaultTransition(defaults.DefaultStageShowAnimationKind, "stage show", TransitionDomains.Stage);
            ValidateDefaultTransition(defaults.DefaultStageClearAnimationKind, "stage clear", TransitionDomains.Stage);
            ValidateDefaultTransition(defaults.DefaultBackdropAnimationKind, "backdrop", TransitionDomains.Stage);
            ValidateDefaultTransition(defaults.DefaultCameraAnimationKind, "camera", TransitionDomains.View);
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
        "character" or "character appearance" or "character hide" or "character space" or
        "object" or "object appearance" or "object hide" or "object space" => TransitionDomains.Actor,
        "camera" or "camera reset" or "camera move" or "camera fx" or "shot use" => TransitionDomains.View,
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
        var supported = id != DirectionTransitionId.Move && transitions!.Supports(id, domain);
        ValidateStateKind(commandName, id);
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

        ValidateWait(input.Wait);
        var domain = ResolveCommandDomain(commandName);
        var isCharacterCommand = domain == TransitionDomains.Actor;
        var kind = input.Via.WasProvided
            ? input.Via.Value
            : defaults.ResolveDefaultAnimationKind(commandName);

        ValidateStateKind(commandName, kind);
        var isSupported = transitions is not null
            ? transitions.Supports(kind, domain)
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
            duration = openBeatDefaults?.DurationSeconds ?? defaults.DefaultCharacterAnimationDurationSeconds;
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

        ValidateWait(input.Wait);
        var openBeatDefaults = beats.OpenBeatDefaults;
        var duration = input.Duration.WasProvided
            ? input.Duration.Value
            : openBeatDefaults?.DurationSeconds ?? (commandName is "character fx" or "object fx" or "backdrop fx"
                ? .4 : defaults.DefaultAnimationDurationSeconds);

        var ease = (input.Ease.WasProvided && !input.Ease.Value.IsEmpty)
            ? input.Ease.Value
            : (openBeatDefaults?.Easing is { IsEmpty: false } beatEase)
                ? beatEase
                : defaults.DefaultEasing.IsEmpty ? EasingId.Linear : defaults.DefaultEasing;

        return new DirectionTransitionSpec(kind, duration, ease).Validate();
    }

    public DirectionTransitionSpec ResolveCameraTransition(
        string commandName,
        TimingArguments input,
        DirectionTransitionId? via,
        CommandExecutionContext context,
        bool reset = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(input);

        return ResolveTransition(commandName, input.ToTransitionArguments(via ?? default), context);
    }

    private void ValidateWait(CommandValue<bool> wait)
    {
        if (beats.HasOpenBeat && wait.WasProvided && wait.Value)
            throw new DirectionBeatException("Beat child commands cannot wait; use 'beat end wait:true' or 'beat join'.");
    }

    private static void ValidateStateKind(string commandName, DirectionTransitionId kind)
    {
        var cut = kind == DirectionTransitionId.Cut;
        var allowed = kind != DirectionTransitionId.Move && commandName switch
        {
            "character" or "object" or "character hide" or "object hide" => cut || kind == DirectionTransitionId.Fade,
            "character appearance" or "object appearance" or "camera" or "camera reset" or "shot use" or "backdrop" => cut || kind == DirectionTransitionId.Dissolve,
            "character space" or "object space" => cut,
            _ => true,
        };
        if (!allowed) throw new DirectionException($"Transition '{kind}' is not supported by '{commandName}'. Use a dedicated move command for motion.");
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
