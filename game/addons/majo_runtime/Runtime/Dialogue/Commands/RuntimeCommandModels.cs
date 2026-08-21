#nullable enable

using Majo.Runtime.Actors;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record BackCommand(
    string ActorId,
    string Appearance,
    double DurationSeconds,
    bool Wait,
    string? GroupId,
    string? OperationId);

public sealed record CharacterCommand(
    string ActorId,
    string Appearance,
    CharacterPositionPreset Position,
    double DurationSeconds,
    bool Wait,
    string? GroupId,
    string? OperationId);

public sealed record HideCommand(
    string ActorId,
    double DurationSeconds,
    bool Wait,
    string? GroupId,
    string? OperationId);

public sealed record AwaitCommand(OperationTarget Target, bool Complete);

public sealed record CancelCommand(OperationTarget Target);
