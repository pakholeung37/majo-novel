#nullable enable

using System;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Stage;

/// <param name="Center">
/// Observation center in reference-scene percentages: 0,0 is bottom-left and
/// 50,50 is the center. Values outside the scene are valid.
/// </param>
public sealed record CameraState(
    Vector2 Center,
    float Zoom,
    float RotationDegrees)
{
    public static CameraState Default { get; } = new(new Vector2(50, 50), 1, 0);

    public CameraState Validate()
    {
        if (!float.IsFinite(Center.X) || !float.IsFinite(Center.Y) ||
            !float.IsFinite(Zoom) || Zoom <= 0 ||
            !float.IsFinite(RotationDegrees))
        {
            throw new ArgumentException("Camera state contains invalid transform values.");
        }
        return this;
    }
}

public sealed record CameraPoseUpdate(
    Vector2? Center = null,
    float? Zoom = null,
    float? RotationDegrees = null)
{
    public CameraPoseUpdate Validate()
    {
        if (Center is { } center &&
            (!float.IsFinite(center.X) || !float.IsFinite(center.Y)))
            throw new ArgumentException("Camera center must be finite.");
        if (Zoom is { } zoom && (!float.IsFinite(zoom) || zoom <= 0))
            throw new ArgumentOutOfRangeException(nameof(Zoom));
        if (RotationDegrees is { } rotation && !float.IsFinite(rotation))
            throw new ArgumentOutOfRangeException(nameof(RotationDegrees));
        if (Center is null && Zoom is null && RotationDegrees is null)
            throw new ArgumentException("Camera pose must change at least one property.");
        return this;
    }

    public CameraState Apply(CameraState state) => state with
    {
        Center = Center ?? state.Center,
        Zoom = Zoom ?? state.Zoom,
        RotationDegrees = RotationDegrees ?? state.RotationDegrees,
    };
}

public enum CameraViewTransitionKind
{
    Cut,
    Move,
    Dissolve,
}

public sealed record CameraViewTransitionSpec(
    CameraViewTransitionKind Kind,
    StageOperationSpec Operation)
{
    public CameraViewTransitionSpec Validate()
    {
        if (!Enum.IsDefined(Kind))
            throw new ArgumentOutOfRangeException(nameof(Kind));
        Operation.Validate();
        if (Kind == CameraViewTransitionKind.Cut && Operation.DurationSeconds > 0)
            throw new ArgumentException(
                "Camera cut duration must be zero.", nameof(Operation));
        return this;
    }
}

public sealed record CameraPoseTransitionRequest(
    CameraPoseUpdate Target,
    CameraViewTransitionSpec Transition)
{
    public CameraPoseTransitionRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(Transition);
        Target.Validate();
        Transition.Validate();
        return this;
    }
}

public enum MotionCurve
{
    Linear,
    SineIn,
    SineOut,
    SineInOut,
    CubicIn,
    CubicOut,
    CubicInOut,
    QuintIn,
    QuintOut,
    QuintInOut,
    BackIn,
    BackOut,
    BackInOut,
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
    double DurationSeconds = RuntimeDefaults.AnimationDurationSeconds,
    bool Skippable = true,
    MotionCurve Curve = MotionCurve.Linear)
{
    public StageOperationSpec Validate()
    {
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(DurationSeconds), "Duration must be finite and non-negative.");
        if (!Enum.IsDefined(Curve))
            throw new ArgumentOutOfRangeException(nameof(Curve));
        return this;
    }
}

public enum CameraShakeAxis
{
    All,
    X,
    Y,
    Diagonal,
}

public sealed record CameraShakeSpec(
    float Strength,
    CameraShakeAxis Axis = CameraShakeAxis.All,
    double Frequency = 20,
    StageOperationSpec? Operation = null)
{
    public StageOperationSpec EffectiveOperation => Operation ?? new StageOperationSpec(0.4);

    public CameraShakeSpec Validate()
    {
        EffectiveOperation.Validate();
        if (!float.IsFinite(Strength) || Strength < 0 ||
            !Enum.IsDefined(Axis) ||
            !double.IsFinite(Frequency) || Frequency <= 0)
        {
            throw new ArgumentException("Shake strength and frequency must be finite and positive.");
        }
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
