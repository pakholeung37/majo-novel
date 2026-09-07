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

    [Export] public string DefaultCharacterAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultLeaveAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultStageShowAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultStageClearAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultBackdropAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultCameraPoseAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultCameraResetAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultShotAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultLayoutAnimationId { get; set; } = string.Empty;
    [Export] public string DefaultDirectionEasingId { get; set; } = string.Empty;

    [Export] public DirectionTransitionKind DefaultCharacterAnimationKind { get; set; } = DirectionTransitionKind.Fade;
    [Export] public DirectionTransitionKind DefaultLeaveAnimationKind { get; set; } = DirectionTransitionKind.Fade;
    [Export] public DirectionTransitionKind DefaultStageShowAnimationKind { get; set; } = DirectionTransitionKind.Cut;
    [Export] public DirectionTransitionKind DefaultStageClearAnimationKind { get; set; } = DirectionTransitionKind.Cut;
    [Export] public DirectionTransitionKind DefaultBackdropAnimationKind { get; set; } = DirectionTransitionKind.Cut;
    [Export] public DirectionTransitionKind DefaultCameraPoseAnimationKind { get; set; } = DirectionTransitionKind.Cut;
    [Export] public DirectionTransitionKind DefaultCameraResetAnimationKind { get; set; } = DirectionTransitionKind.Cut;
    [Export] public DirectionTransitionKind DefaultShotAnimationKind { get; set; } = DirectionTransitionKind.Cut;
    [Export] public DirectionTransitionKind DefaultLayoutAnimationKind { get; set; } = DirectionTransitionKind.Cut;
    [Export] public DirectionEase DefaultDirectionAnimationEase { get; set; } = DirectionEase.Linear;
    [Export] public MotionCurve DefaultStageAnimationCurve { get; set; } = MotionCurve.Linear;

    public DirectionTransitionId ResolveCharacterAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultCharacterAnimationId)
            ? new DirectionTransitionId(DefaultCharacterAnimationId)
            : DirectionTransitionId.FromKind(DefaultCharacterAnimationKind);

    public DirectionTransitionId ResolveLeaveAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultLeaveAnimationId)
            ? new DirectionTransitionId(DefaultLeaveAnimationId)
            : DirectionTransitionId.FromKind(DefaultLeaveAnimationKind);

    public DirectionTransitionId ResolveStageShowAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultStageShowAnimationId)
            ? new DirectionTransitionId(DefaultStageShowAnimationId)
            : DirectionTransitionId.FromKind(DefaultStageShowAnimationKind);

    public DirectionTransitionId ResolveStageClearAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultStageClearAnimationId)
            ? new DirectionTransitionId(DefaultStageClearAnimationId)
            : DirectionTransitionId.FromKind(DefaultStageClearAnimationKind);

    public DirectionTransitionId ResolveBackdropAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultBackdropAnimationId)
            ? new DirectionTransitionId(DefaultBackdropAnimationId)
            : DirectionTransitionId.FromKind(DefaultBackdropAnimationKind);

    public DirectionTransitionId ResolveCameraPoseAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultCameraPoseAnimationId)
            ? new DirectionTransitionId(DefaultCameraPoseAnimationId)
            : DirectionTransitionId.FromKind(DefaultCameraPoseAnimationKind);

    public DirectionTransitionId ResolveCameraResetAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultCameraResetAnimationId)
            ? new DirectionTransitionId(DefaultCameraResetAnimationId)
            : DirectionTransitionId.FromKind(DefaultCameraResetAnimationKind);

    public DirectionTransitionId ResolveShotAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultShotAnimationId)
            ? new DirectionTransitionId(DefaultShotAnimationId)
            : DirectionTransitionId.FromKind(DefaultShotAnimationKind);

    public DirectionTransitionId ResolveLayoutAnimationId() =>
        !string.IsNullOrWhiteSpace(DefaultLayoutAnimationId)
            ? new DirectionTransitionId(DefaultLayoutAnimationId)
            : DirectionTransitionId.FromKind(DefaultLayoutAnimationKind);

    public EasingId ResolveDirectionEasingId() =>
        !string.IsNullOrWhiteSpace(DefaultDirectionEasingId)
            ? new EasingId(DefaultDirectionEasingId)
            : DirectionTransitionSpec.FromDirectionEase(DefaultDirectionAnimationEase);
}
