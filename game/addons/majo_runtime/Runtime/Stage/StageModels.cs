#nullable enable

using System;
using Godot;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Stage;

public sealed record CameraState(
    Vector2 Position,
    float Zoom,
    float RotationDegrees)
{
    public static CameraState Default { get; } = new(Vector2.Zero, 1, 0);

    public CameraState Validate()
    {
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) ||
            !float.IsFinite(Zoom) || Zoom <= 0 ||
            !float.IsFinite(RotationDegrees))
        {
            throw new ArgumentException("Camera state contains invalid transform values.");
        }
        return this;
    }
}

public sealed record ScreenEffectState(
    Color OverlayColor,
    float OverlayOpacity,
    float LetterboxRatio)
{
    public static ScreenEffectState Default { get; } = new(Colors.Black, 0, 0);

    public ScreenEffectState Validate()
    {
        if (!float.IsFinite(OverlayOpacity) || OverlayOpacity is < 0 or > 1 ||
            !float.IsFinite(LetterboxRatio) || LetterboxRatio is < 0 or > 0.5f)
        {
            throw new ArgumentException("Screen effect opacity and letterbox ratio are out of range.");
        }
        return this;
    }
}

public enum StageTransitionKind
{
    Fade,
    Crossfade,
    WipeLeft,
    WipeRight,
    Custom,
}

public sealed record StageOperationSpec(
    double DurationSeconds = 0.35,
    string? OperationId = null,
    string? GroupId = null,
    bool Skippable = true)
{
    public StageOperationSpec Validate()
    {
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(DurationSeconds), "Duration must be finite and non-negative.");
        if (OperationId is not null && string.IsNullOrWhiteSpace(OperationId))
            throw new ArgumentException("Operation ID cannot be blank.");
        if (GroupId is not null && string.IsNullOrWhiteSpace(GroupId))
            throw new ArgumentException("Group ID cannot be blank.");
        return this;
    }
}

public sealed record CameraShakeSpec(
    Vector2 Strength,
    double Frequency = 20,
    StageOperationSpec? Operation = null)
{
    public StageOperationSpec EffectiveOperation => Operation ?? new StageOperationSpec(0.4);

    public CameraShakeSpec Validate()
    {
        EffectiveOperation.Validate();
        if (!float.IsFinite(Strength.X) || !float.IsFinite(Strength.Y) ||
            !double.IsFinite(Frequency) || Frequency <= 0)
            throw new ArgumentException("Shake strength and frequency must be finite and positive.");
        return this;
    }
}

public sealed record StageTransitionRequest(
    StageTransitionKind Kind,
    Color Color,
    StageOperationSpec Operation,
    string? TargetAppearance = null,
    string? ProviderId = null,
    IResourceGroupLease? Resources = null)
{
    public StageTransitionRequest Validate()
    {
        Operation.Validate();
        if (Kind == StageTransitionKind.Custom)
            ArgumentException.ThrowIfNullOrWhiteSpace(ProviderId);
        return this;
    }
}

public sealed record StageStableState(
    CameraState Camera,
    ScreenEffectState ScreenEffect,
    string? Appearance);
