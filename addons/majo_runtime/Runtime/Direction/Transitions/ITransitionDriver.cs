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
    Texture2D? TargetTexture = null);

public interface ITransitionDriver
{
    DirectionTransitionId Id { get; }

    TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen;

    void Setup(VisualTransitionContext context);
}
