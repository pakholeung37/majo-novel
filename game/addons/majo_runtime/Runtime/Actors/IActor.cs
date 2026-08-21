#nullable enable

using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors;

public interface IActor
{
    string Id { get; }

    string Appearance { get; }

    bool Visible { get; }

    Vector2 Position { get; }

    Color Tint { get; }

    float Opacity { get; }

    Node SceneNode { get; }

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

    IOperationHandle ChangeOpacity(
        float opacity,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle Show(
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    IOperationHandle Hide(
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);

    void Destroy();
}

public interface ICharacterActor : IActor
{
    Sprite2D View { get; }

    CharacterPositionPreset PositionPreset { get; }

    IOperationHandle ChangePosition(
        CharacterPositionPreset preset,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);
}

public interface IBackgroundActor : IActor
{
    TextureRect CurrentView { get; }

    IOperationHandle CrossFadeAppearance(
        string appearance,
        double durationSeconds,
        string? operationId = null,
        string? groupId = null);
}
