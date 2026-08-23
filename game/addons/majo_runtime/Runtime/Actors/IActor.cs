#nullable enable

using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors;

public interface IActor
{
    string Id { get; }

    string FactoryId => string.Empty;

    string Appearance { get; }

    string TargetAppearance => Appearance;

    bool Visible { get; }

    bool TargetVisible => Visible;

    Vector2 Position { get; }

    Vector2 TargetPosition => Position;

    Color Tint { get; }

    float Opacity { get; }

    float TargetOpacity => Opacity;

    void SetAppearance(string appearance);

    void SetVisible(bool visible);

    void SetPosition(Vector2 position);

    void SetTint(Color tint);

    void SetOpacity(float opacity);

    IOperationHandle ChangePosition(
        Vector2 position,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle ChangePosition(
        Vector2 position,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null) => ChangePosition(
            position, animation.DurationSeconds, operationId, groupId);

    IOperationHandle ChangeOpacity(
        float opacity,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle ChangeOpacity(
        float opacity,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null) => ChangeOpacity(
            opacity, animation.DurationSeconds, operationId, groupId);

    IOperationHandle Show(
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle Show(
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null) => Show(
            animation.DurationSeconds, operationId, groupId);

    IOperationHandle Hide(
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle Hide(
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null) => Hide(
            animation.DurationSeconds, operationId, groupId);

    void Destroy();
}

public interface IGodotActorView
{
    Node SceneNode { get; }
}

public interface ICharacterActor : IActor, IGodotActorView
{
    Sprite2D View { get; }

    CharacterPositionPreset PositionPreset { get; }

    IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null) => ChangePosition(
            preset, animation.DurationSeconds, operationId, groupId);
}

public interface IBackgroundActor : IActor, IGodotActorView
{
    TextureRect CurrentView { get; }

    IOperationHandle CrossFadeAppearance(
        string appearance,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle CrossFadeAppearance(
        string appearance,
        AnimationSpec animation,
        string? operationId = null,
        string? groupId = null) => CrossFadeAppearance(
            appearance, animation.DurationSeconds, operationId, groupId);
}
