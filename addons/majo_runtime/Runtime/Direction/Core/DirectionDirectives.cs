#nullable enable

using System;
using System.Collections.Generic;

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
