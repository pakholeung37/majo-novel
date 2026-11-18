#nullable enable

using Godot;

namespace Majo.Runtime.Direction.Camera;

public readonly record struct CameraPoseDelta(
    Vector2 CenterOffset,
    float ZoomMultiplier = 1.0f,
    float RotationDegreesOffset = 0.0f)
{
    public static CameraPoseDelta Identity { get; } = new(Vector2.Zero, 1.0f, 0.0f);

    public CameraPoseDelta Combine(CameraPoseDelta other) => new(
        CenterOffset + other.CenterOffset,
        ZoomMultiplier * other.ZoomMultiplier,
        RotationDegreesOffset + other.RotationDegreesOffset);
}
