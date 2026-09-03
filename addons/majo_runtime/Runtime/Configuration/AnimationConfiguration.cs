using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class AnimationConfiguration : Resource
{
    [Export] public double DefaultAnimationDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultCharacterAnimationDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultStageTransitionDurationSeconds { get; set; } = RuntimeDefaults.TransitionDurationSeconds;
    [Export] public double DefaultStageEffectDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultCameraShakeDurationSeconds { get; set; } = RuntimeDefaults.CameraShakeDurationSeconds;
    [Export] public double DefaultFlashDurationSeconds { get; set; } = RuntimeDefaults.FlashDurationSeconds;
    [Export] public double DefaultCurtainDurationSeconds { get; set; } = RuntimeDefaults.CurtainDurationSeconds;
    [Export] public double DefaultAudioFadeDurationSeconds { get; set; } = RuntimeDefaults.AnimationDurationSeconds;
    [Export] public double DefaultVoiceFadeDurationSeconds { get; set; } = RuntimeDefaults.ImmediateDurationSeconds;

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
}
