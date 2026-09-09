#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Actors;

/// <summary>Unqueued, complete base-space access for Direction's single transform slot.</summary>
public interface IActorTransformTarget
{
    Vector2 ScenePosition { get; }
    Vector2 Scale { get; }
    float RotationDegrees { get; }
    void SetScenePosition(Vector2 scenePosition);
    void SetScale(Vector2 scale);
    void SetRotationDegrees(float rotation);
}

public interface IActorAppearanceTarget
{
    IDisposable? RetainVisualPreparation() => null;
    Task PrepareAppearanceAsync(string appearance, CancellationToken cancellationToken = default);
    void SetAppearance(string appearance);
    Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(string appearance,
        AnimationSpec animation, ITransitionDriver driver, CancellationToken cancellationToken = default);
}

public interface IPreparedActorAppearance : IPreparedVisualTransition
{
    void Commit();
    void Finish();
}

public interface IActorVisibilityTarget
{
    float Opacity { get; }
    void SetOpacity(float opacity);
    void SetVisible(bool visible);
}
