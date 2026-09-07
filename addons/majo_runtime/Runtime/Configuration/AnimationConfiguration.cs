using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Stage;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class AnimationConfiguration : Resource
{
    [Export] public double DefaultAnimationDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultCharacterAnimationDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultStageTransitionDurationSeconds { get; set; } = RuntimeDefaults.TransitionDurationSeconds;
    [Export] public double DefaultStageEffectDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultCurtainDurationSeconds { get; set; } = RuntimeDefaults.CurtainDurationSeconds;
    [Export] public double DefaultAudioFadeDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultVoiceFadeDurationSeconds { get; set; } = RuntimeDefaults.ImmediateDurationSeconds;

    [Export] public string DefaultCharacterAnimationId { get; set; } = "fade";
    [Export] public string DefaultLeaveAnimationId { get; set; } = "fade";
    [Export] public string DefaultStageShowAnimationId { get; set; } = "cut";
    [Export] public string DefaultStageClearAnimationId { get; set; } = "cut";
    [Export] public string DefaultBackdropAnimationId { get; set; } = "cut";
    [Export] public string DefaultCameraPoseAnimationId { get; set; } = "cut";
    [Export] public string DefaultCameraResetAnimationId { get; set; } = "cut";
    [Export] public string DefaultShotAnimationId { get; set; } = "cut";
    [Export] public string DefaultLayoutAnimationId { get; set; } = "cut";
    [Export] public string DefaultDirectionEasingId { get; set; } = "linear";
    [Export] public MotionCurve DefaultStageAnimationCurve { get; set; } = MotionCurve.Linear;

    public DirectionTransitionId ResolveCharacterAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultCharacterAnimationId) ? DefaultCharacterAnimationId : "fade");

    public DirectionTransitionId ResolveLeaveAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultLeaveAnimationId) ? DefaultLeaveAnimationId : "fade");

    public DirectionTransitionId ResolveStageShowAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultStageShowAnimationId) ? DefaultStageShowAnimationId : "cut");

    public DirectionTransitionId ResolveStageClearAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultStageClearAnimationId) ? DefaultStageClearAnimationId : "cut");

    public DirectionTransitionId ResolveBackdropAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultBackdropAnimationId) ? DefaultBackdropAnimationId : "cut");

    public DirectionTransitionId ResolveCameraPoseAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultCameraPoseAnimationId) ? DefaultCameraPoseAnimationId : "cut");

    public DirectionTransitionId ResolveCameraResetAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultCameraResetAnimationId) ? DefaultCameraResetAnimationId : "cut");

    public DirectionTransitionId ResolveShotAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultShotAnimationId) ? DefaultShotAnimationId : "cut");

    public DirectionTransitionId ResolveLayoutAnimationId() =>
        new(!string.IsNullOrWhiteSpace(DefaultLayoutAnimationId) ? DefaultLayoutAnimationId : "cut");

    public EasingId ResolveDirectionEasingId() =>
        new(!string.IsNullOrWhiteSpace(DefaultDirectionEasingId) ? DefaultDirectionEasingId : "linear");
}
