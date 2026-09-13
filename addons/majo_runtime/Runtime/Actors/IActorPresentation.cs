#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Direction.Transitions;

namespace Majo.Runtime.Actors;

public interface ISceneActor :
    IActor,
    IGodotActorView,
    IActorTransformTarget,
    IActorAppearanceTarget,
    IActorVisibilityTarget,
    Majo.Runtime.State.IStateMutationParticipant
{
}

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
    string ResolveAppearance(string sourceAppearance, string appearance) => appearance;
    IDisposable? RetainVisualPreparation() => null;
    Task<IPreparedActorAppearance> PrepareAppearanceTransitionAsync(
        string sourceAppearance,
        string appearance,
        AnimationSpec animation, ITransitionDriver driver, CancellationToken cancellationToken = default);
}

public interface IPreparedActorAppearance : IPreparedVisualTransition
{
    string ResolvedAppearance { get; }
    void Commit();
    void Finish();
}

/// <summary>
/// Prepares actor-local live transitions. Input nodes must keep their animation/simulation
/// with the provided trees; the host renders them until the transition releases them. They are temporary content
/// whose lifetime is transferred to the returned prepared transition.
/// </summary>
public interface IActorAppearanceTransitionService
{
    Task<IPreparedActorAppearance> PrepareForCommitAsync(
        Node2D owner,
        CanvasItem sourceContent,
        CanvasItem targetContent,
        Rect2 bounds,
        Action<bool> showOriginal,
        Action<Sprite2D?> publishOverlay,
        Action publishTarget,
        Action finish,
        string resolvedAppearance,
        ITransitionDriver driver,
        AnimationSpec animation,
        CancellationToken cancellationToken = default);

    /// <summary>Explicitly freezes both inputs for callers that require fixed frames.</summary>
    Task<IPreparedVisualTransition> PrepareSnapshotAsync(
        Node2D owner,
        CanvasItem sourceContent,
        CanvasItem targetContent,
        Rect2 bounds,
        float opacity,
        Action<bool> showOriginal,
        Action<Sprite2D?> publishOverlay,
        ITransitionDriver driver,
        AnimationSpec animation,
        CancellationToken cancellationToken = default);
}

public interface IActorVisibilityTarget
{
}
