using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class AnimationConfiguration : Resource, IValidatableConfiguration
{
    [Export] public double DefaultAnimationDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultCharacterAnimationDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultStageTransitionDurationSeconds { get; set; } = RuntimeDefaults.TransitionDurationSeconds;
    [Export] public double DefaultStageEffectDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultCurtainDurationSeconds { get; set; } = RuntimeDefaults.CurtainDurationSeconds;
    [Export] public double DefaultAudioFadeDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultVoiceFadeDurationSeconds { get; set; } = RuntimeDefaults.ImmediateDurationSeconds;

    [Export] public string DefaultCharacterAnimationId { get; set; } = "fade";
    [Export] public string DefaultCharacterAppearanceAnimationId { get; set; } = "dissolve";
    [Export] public string DefaultActorHideAnimationId { get; set; } = "fade";
    [Export] public string DefaultStageClearAnimationId { get; set; } = "cut";
    [Export] public string DefaultBackdropAnimationId { get; set; } = "cut";
    [Export] public string DefaultCameraAnimationId { get; set; } = "cut";
    [Export] public string DefaultCameraResetAnimationId { get; set; } = "cut";
    [Export] public string DefaultShotAnimationId { get; set; } = "cut";
    [Export] public string DefaultScreenAnimationId { get; set; } = "cut";
    [Export] public string DefaultDirectionEasingId { get; set; } = "linear";

    public DirectionTransitionId ResolveCharacterAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultCharacterAnimationId) ? DefaultCharacterAnimationId : "fade");

    public DirectionTransitionId ResolveCharacterAppearanceAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultCharacterAppearanceAnimationId) ? DefaultCharacterAppearanceAnimationId : "dissolve");

    public DirectionTransitionId ResolveActorHideAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultActorHideAnimationId) ? DefaultActorHideAnimationId : "fade");

    public DirectionTransitionId ResolveStageClearAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultStageClearAnimationId) ? DefaultStageClearAnimationId : "cut");

    public DirectionTransitionId ResolveBackdropAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultBackdropAnimationId) ? DefaultBackdropAnimationId : "cut");

    public DirectionTransitionId ResolveCameraAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultCameraAnimationId) ? DefaultCameraAnimationId : "cut");

    public DirectionTransitionId ResolveCameraResetAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultCameraResetAnimationId) ? DefaultCameraResetAnimationId : "cut");

    public DirectionTransitionId ResolveShotAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultShotAnimationId) ? DefaultShotAnimationId : "cut");

    public DirectionTransitionId ResolveScreenAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultScreenAnimationId) ? DefaultScreenAnimationId : "cut");

    public EasingId ResolveDirectionEasingId() =>
        new(!string.IsNullOrWhiteSpace(DefaultDirectionEasingId) ? DefaultDirectionEasingId : "linear");

    public void Validate(ConfigurationValidationContext context)
    {
        System.ArgumentNullException.ThrowIfNull(context);

        var durations = new (string Name, double Value)[]
        {
            (nameof(DefaultAnimationDurationSeconds), DefaultAnimationDurationSeconds),
            (nameof(DefaultCharacterAnimationDurationSeconds), DefaultCharacterAnimationDurationSeconds),
            (nameof(DefaultStageTransitionDurationSeconds), DefaultStageTransitionDurationSeconds),
            (nameof(DefaultStageEffectDurationSeconds), DefaultStageEffectDurationSeconds),
            (nameof(DefaultCurtainDurationSeconds), DefaultCurtainDurationSeconds),
            (nameof(DefaultAudioFadeDurationSeconds), DefaultAudioFadeDurationSeconds),
            (nameof(DefaultVoiceFadeDurationSeconds), DefaultVoiceFadeDurationSeconds),
        };

        foreach (var (name, value) in durations)
        {
            if (!double.IsFinite(value) || value < 0)
            {
                context.AddIssue(name, "Value must be finite and non-negative.");
            }
        }

        var animationIds = new (string Name, string Value)[]
        {
            (nameof(DefaultCharacterAnimationId), DefaultCharacterAnimationId),
            (nameof(DefaultCharacterAppearanceAnimationId), DefaultCharacterAppearanceAnimationId),
            (nameof(DefaultActorHideAnimationId), DefaultActorHideAnimationId),
            (nameof(DefaultStageClearAnimationId), DefaultStageClearAnimationId),
            (nameof(DefaultBackdropAnimationId), DefaultBackdropAnimationId),
            (nameof(DefaultCameraAnimationId), DefaultCameraAnimationId),
            (nameof(DefaultCameraResetAnimationId), DefaultCameraResetAnimationId),
            (nameof(DefaultShotAnimationId), DefaultShotAnimationId),
            (nameof(DefaultScreenAnimationId), DefaultScreenAnimationId),
        };

        foreach (var (propName, animId) in animationIds)
        {
            if (string.IsNullOrWhiteSpace(animId))
            {
                context.AddIssue(propName, "Default animation ID cannot be empty.");
            }
        }

        if (string.IsNullOrWhiteSpace(DefaultDirectionEasingId))
        {
            context.AddIssue(nameof(DefaultDirectionEasingId), "Default direction easing ID cannot be empty.");
        }
    }
}
