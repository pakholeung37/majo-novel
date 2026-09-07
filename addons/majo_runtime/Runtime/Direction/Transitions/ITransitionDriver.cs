#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Transitions;

[Flags]
public enum TransitionDomains
{
    None = 0,
    View = 1,
    Stage = 2,
    Screen = 4,
    Actor = 8,
}

public sealed record VisualTransitionContext(
    CanvasItem Surface,
    DirectionTransitionSpec Spec,
    Tween Tween,
    Func<bool>? IsActive = null,
    Texture2D? SourceTexture = null,
    Texture2D? TargetTexture = null,
    Func<float, float>? MapProgress = null,
    Action<Exception>? OnFailure = null)
{
    public void AnimateProgress(Action<float> apply)
    {
        Tween.TweenMethod(Callable.From<float>(progress =>
        {
            if (IsActive?.Invoke() == false) return;
            try { apply(MapProgress?.Invoke(progress) ?? progress); }
            catch (Exception exception)
            {
                Tween.Kill();
                if (OnFailure is null) throw;
                OnFailure(exception);
            }
        }), 0.0f, 1.0f, Spec.DurationSeconds);
    }
}

public interface ITransitionDriver
{
    DirectionTransitionId Id { get; }

    TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen;

    bool SupportsCustomEasing => false;

    void Setup(VisualTransitionContext context);
}
