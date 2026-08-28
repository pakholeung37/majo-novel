#nullable enable

using System;
using Godot;
using Majo.Runtime.Operations;
using System.Threading;
using System.Threading.Tasks;

namespace Majo.Runtime.Actors;

public static class BackgroundSlotIds
{
    public const string Main = "main";

    public static string Normalize(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
        {
            throw new ActorException("Background slot cannot be empty.");
        }

        return slot.Trim().ToLowerInvariant();
    }
}

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

    Task PrepareAppearanceAsync(
        string appearance,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

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

    Vector2 Scale { get; }

    CharacterPositionPreset PositionPreset { get; }

    void SetScale(Vector2 scale);

    void SetScenePosition(Vector2 scenePosition);

    void SetPosition(CharacterPositionPreset preset);

    IOperationHandle ChangeAppearance(
        string appearance,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle ChangeScenePosition(
        Vector2 scenePosition,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

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
    string Slot => BackgroundSlotIds.Main;

    void SetSlot(string slot)
    {
        if (!string.Equals(
                BackgroundSlotIds.Normalize(slot),
                BackgroundSlotIds.Main,
                StringComparison.Ordinal))
        {
            throw new ActorException(
                $"Background actor '{Id}' only supports the '{BackgroundSlotIds.Main}' slot.");
        }
    }

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
