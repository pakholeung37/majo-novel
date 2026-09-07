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

public interface IPreparedVisualTransition : IDisposable
{
    void ApplyProgress(float progress);
}

public sealed record VisualTransitionContext(
    CanvasItem Surface,
    DirectionTransitionSpec Spec,
    Texture2D? SourceTexture = null,
    Texture2D? TargetTexture = null,
    bool SourceIsPremultiplied = false,
    bool TargetIsPremultiplied = false,
    Func<bool>? IsActive = null,
    Action<Exception>? OnFailure = null);

public interface ITransitionDriver
{
    DirectionTransitionId Id { get; }

    TransitionDomains Domains => TransitionDomains.View | TransitionDomains.Stage | TransitionDomains.Screen;

    bool SupportsCustomEasing => true;

    IPreparedVisualTransition Prepare(VisualTransitionContext context);
}
