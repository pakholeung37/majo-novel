#nullable enable

using Godot;
using Majo.Runtime.Actors;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record BackCommand(
    string ActorId,
    string Appearance,
    double DurationSeconds,
    bool Wait,
    string? GroupId,
    string? OperationId,
    string Slot = BackgroundSlotIds.Main);

public sealed record CharacterCommand(
    string ActorId,
    string Appearance,
    CharacterPositionPreset Position,
    double DurationSeconds,
    bool Wait,
    string? GroupId,
    string? OperationId,
    Vector2? ScenePosition = null,
    float? Scale = null,
    int? ZIndex = null);

public sealed record HideCommand(
    string ActorId,
    double DurationSeconds,
    bool Wait,
    string? GroupId,
    string? OperationId);
