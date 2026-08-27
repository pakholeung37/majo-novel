#nullable enable

using System;
using Godot;
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

public enum StageCurtainPosition
{
    Open,
    Closed,
}

public sealed record StageCurtainState(
    Color Color,
    float Opacity)
{
    public static StageCurtainState Default { get; } = new(Colors.Black, 0);

    public StageCurtainState Validate()
    {
        if (!float.IsFinite(Color.R) || !float.IsFinite(Color.G) ||
            !float.IsFinite(Color.B) || !float.IsFinite(Color.A) ||
            !float.IsFinite(Opacity) || Opacity is < 0 or > 1)
        {
            throw new ArgumentException("Curtain color or opacity is out of range.");
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
    bool Skippable = true,
    MotionCurve Curve = MotionCurve.Linear)
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

public sealed record StageStableState(
    CameraState Camera,
    ScreenEffectState ScreenEffect,
    StageCurtainState Curtain,
    string? Appearance)
{
    public StageStableState(
        CameraState camera,
        ScreenEffectState screenEffect,
        string? appearance)
        : this(camera, screenEffect, StageCurtainState.Default, appearance)
    {
    }
}
