#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Direction.Core;

public readonly struct DirectionValue<T>
{
    private readonly T value;

    private DirectionValue(T value)
    {
        this.value = value;
        HasValue = true;
    }

    public bool HasValue { get; }

    public T Value => HasValue
        ? value
        : throw new InvalidOperationException("The direction value was not specified.");

    public static DirectionValue<T> From(T value) => new(value);
}

public sealed record DirectionSourceLocation(string Node, int Line, int CommandIndex)
{
    public override string ToString() => $"{Node}:{Line}#{CommandIndex}";
}

public enum DirectionTransitionKind
{
    Cut,
    Move,
    Fade,
    Dissolve,
}

public enum DirectionEase
{
    Linear,
    SineIn,
    SineOut,
    SineInOut,
    CubicIn,
    CubicOut,
    CubicInOut,
    BackIn,
    BackOut,
    BackInOut,
}

public enum DirectionEaseCurve
{
    Linear,
    Sine,
    Cubic,
    Back,
}

public enum DirectionEaseMode
{
    In,
    Out,
    InOut,
}

public readonly record struct DirectionEaseParts(
    DirectionEaseCurve Curve,
    DirectionEaseMode Mode);

public static class DirectionEaseExtensions
{
    public static DirectionEaseParts Decompose(this DirectionEase ease)
    {
        if (ease == DirectionEase.Linear)
            return new(DirectionEaseCurve.Linear, DirectionEaseMode.InOut);

        var name = ease.ToString();
        foreach (var (suffix, mode) in new[]
        {
            ("InOut", DirectionEaseMode.InOut),
            ("In", DirectionEaseMode.In),
            ("Out", DirectionEaseMode.Out),
        })
        {
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var curveName = name[..^suffix.Length];
            if (Enum.TryParse<DirectionEaseCurve>(curveName, out var curve))
                return new(curve, mode);
        }

        throw new ArgumentOutOfRangeException(nameof(ease), ease, null);
    }
}

public sealed record DirectionTransitionSpec(
    DirectionTransitionKind Kind,
    double DurationSeconds,
    DirectionEase Ease = DirectionEase.Linear,
    bool Skippable = true)
{
    public static DirectionTransitionSpec Cut { get; } =
        new(DirectionTransitionKind.Cut, 0);

    public DirectionTransitionSpec Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        if (!Enum.IsDefined(Ease)) throw new ArgumentOutOfRangeException(nameof(Ease));
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        if (Kind == DirectionTransitionKind.Cut && DurationSeconds != 0)
            throw new ArgumentException("A cut transition cannot have a duration.");
        return this;
    }
}

public interface IDirectionDirective
{
    DirectionSourceLocation? SourceLocation { get; }
}

public interface IDurableDirectionPatch : IDirectionDirective
{
    DirectionTransitionSpec Transition { get; }
}

public interface ITransientDirectionCue : IDirectionDirective;

public sealed record BackdropPatch(
    StageId StageId,
    BackdropState Backdrop,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record ActorPatch(
    StageId StageId,
    ActorId ActorId,
    DirectionValue<DirectionPoint> Position,
    DirectionValue<string?> Appearance,
    DirectionValue<float> Scale,
    DirectionValue<int> Z,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch
{
    public static ActorPatch Place(
        StageId stageId,
        ActorId actorId,
        DirectionTransitionSpec? transition = null,
        DirectionSourceLocation? sourceLocation = null) => new(
            stageId,
            actorId,
            default,
            default,
            default,
            default,
            transition ?? DirectionTransitionSpec.Cut,
            sourceLocation);
}

public sealed record ActorLeavePatch(
    StageId StageId,
    ActorId ActorId,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record ViewPatch(
    ViewId ViewId,
    DirectionValue<ViewSource> Source,
    DirectionValue<DirectionPoint> Center,
    DirectionValue<float> Zoom,
    DirectionValue<float> RotationDegrees,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record ScreenPatch(
    LayoutId Layout,
    IReadOnlyDictionary<LayoutSlotId, ViewId> Bindings,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record DirectionCue(
    string Name,
    IReadOnlyDictionary<string, string> Arguments,
    DirectionSourceLocation? SourceLocation = null) : ITransientDirectionCue;

public sealed record ShakeCue(
    float Strength, CameraShakeAxis Axis, int Frequency, double DurationSeconds,
    DirectionSourceLocation? SourceLocation = null) : ITransientDirectionCue;

public sealed record FlashCue(
    DirectionColor Color, float Intensity, double DurationSeconds,
    DirectionSourceLocation? SourceLocation = null) : ITransientDirectionCue;

public sealed record CurtainCue(
    bool Open, DirectionColor Color, double DurationSeconds,
    DirectionSourceLocation? SourceLocation = null) : ITransientDirectionCue;
