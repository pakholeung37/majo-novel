#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Transitions;

public sealed class FadeTransitionDriver : ITransitionDriver
{
    public DirectionTransitionId Id => DirectionTransitionId.Fade;
    public TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen | TransitionDomains.Actor;

    public void Setup(VisualTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Spec.DurationSeconds <= 0)
        {
            return;
        }

        var initialAlpha = context.Surface.Modulate.A;
        context.Tween.TweenMethod(
            Callable.From<float>(progress =>
            {
                if (context.IsActive?.Invoke() != false && GodotObject.IsInstanceValid(context.Surface))
                {
                    context.Surface.Modulate = new Color(
                        context.Surface.Modulate,
                        (1.0f - progress) * initialAlpha);
                }
            }),
            0.0f,
            1.0f,
            context.Spec.DurationSeconds);
    }
}

public sealed class DissolveTransitionDriver : ITransitionDriver
{
    public DirectionTransitionId Id => DirectionTransitionId.Dissolve;
    public TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen | TransitionDomains.Actor;

    public void Setup(VisualTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Spec.DurationSeconds <= 0)
        {
            return;
        }

        var initialAlpha = context.Surface.Modulate.A;
        context.Tween.TweenMethod(
            Callable.From<float>(progress =>
            {
                if (context.IsActive?.Invoke() != false && GodotObject.IsInstanceValid(context.Surface))
                {
                    context.Surface.Modulate = new Color(
                        context.Surface.Modulate,
                        (1.0f - progress) * initialAlpha);
                }
            }),
            0.0f,
            1.0f,
            context.Spec.DurationSeconds);
    }
}

public sealed class CutTransitionDriver : ITransitionDriver
{
    public DirectionTransitionId Id => DirectionTransitionId.Cut;
    public TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen | TransitionDomains.Actor;

    public void Setup(VisualTransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (GodotObject.IsInstanceValid(context.Surface))
        {
            context.Surface.Modulate = new Color(context.Surface.Modulate, 0f);
        }
    }
}
