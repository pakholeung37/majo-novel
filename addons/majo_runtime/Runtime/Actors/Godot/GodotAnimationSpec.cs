using System;
using Godot;

namespace Majo.Runtime.Actors.Godot;

internal static class GodotAnimationSpec
{
    public static Tween.TransitionType Transition(AnimationTransition transition) =>
        transition switch
        {
            AnimationTransition.Linear => Tween.TransitionType.Linear,
            AnimationTransition.Sine => Tween.TransitionType.Sine,
            AnimationTransition.Cubic => Tween.TransitionType.Cubic,
            AnimationTransition.Quint => Tween.TransitionType.Quint,
            AnimationTransition.Back => Tween.TransitionType.Back,
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

    public static Tween.EaseType Ease(AnimationEase ease) => ease switch
    {
        AnimationEase.In => Tween.EaseType.In,
        AnimationEase.Out => Tween.EaseType.Out,
        AnimationEase.InOut => Tween.EaseType.InOut,
        AnimationEase.OutIn => Tween.EaseType.OutIn,
        _ => throw new ArgumentOutOfRangeException(nameof(ease)),
    };
}
