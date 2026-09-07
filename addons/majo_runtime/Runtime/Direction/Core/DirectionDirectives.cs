#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Stage;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Direction.Core;

public readonly struct DirectionValue<T>
{
    private readonly T value;

    public DirectionValue(T value)
    {
        this.value = value;
        HasValue = true;
    }

    public bool HasValue { get; }

    public T Value => HasValue
        ? value
        : throw new InvalidOperationException("The direction value was not specified.");

    public static implicit operator DirectionValue<T>(T value) => new(value);
}

public static class DirectionValue
{
    public static DirectionValue<T> From<T>(T value) => new(value);
}

public sealed record DirectionSourceLocation(string Node, int Line, int CommandIndex)
{
    public override string ToString() => $"{Node}:{Line}#{CommandIndex}";
}

[Obsolete("Use DirectionTransitionId instead.")]
public enum DirectionTransitionKind
{
    Cut,
    Move,
    Fade,
    Dissolve,
}

public readonly record struct DirectionTransitionId : IEquatable<DirectionTransitionId>, IComparable<DirectionTransitionId>
{
    public string Value { get; }

    public DirectionTransitionId(string? value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    public static DirectionTransitionId Cut { get; } = new("cut");
    public static DirectionTransitionId Move { get; } = new("move");
    public static DirectionTransitionId Fade { get; } = new("fade");
    public static DirectionTransitionId Dissolve { get; } = new("dissolve");

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public bool Equals(DirectionTransitionId other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);

    public int CompareTo(DirectionTransitionId other) =>
        string.Compare(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator DirectionTransitionId(string? value) => new(value);

#pragma warning disable CS0618
    public static implicit operator DirectionTransitionId(DirectionTransitionKind kind) => FromKind(kind);

    public static DirectionTransitionId FromKind(DirectionTransitionKind kind) => kind switch
    {
        DirectionTransitionKind.Cut => Cut,
        DirectionTransitionKind.Move => Move,
        DirectionTransitionKind.Fade => Fade,
        DirectionTransitionKind.Dissolve => Dissolve,
        _ => new(kind.ToString().ToLowerInvariant()),
    };

    public bool TryToKind(out DirectionTransitionKind kind)
    {
        if (Equals(Cut)) { kind = DirectionTransitionKind.Cut; return true; }
        if (Equals(Move)) { kind = DirectionTransitionKind.Move; return true; }
        if (Equals(Fade)) { kind = DirectionTransitionKind.Fade; return true; }
        if (Equals(Dissolve)) { kind = DirectionTransitionKind.Dissolve; return true; }
        kind = default;
        return false;
    }

    public static bool operator ==(DirectionTransitionId id, DirectionTransitionKind kind) =>
        id.Equals(FromKind(kind));

    public static bool operator !=(DirectionTransitionId id, DirectionTransitionKind kind) =>
        !id.Equals(FromKind(kind));

    public static bool operator ==(DirectionTransitionKind kind, DirectionTransitionId id) =>
        id.Equals(FromKind(kind));

    public static bool operator !=(DirectionTransitionKind kind, DirectionTransitionId id) =>
        !id.Equals(FromKind(kind));
#pragma warning restore CS0618

    public static bool operator <(DirectionTransitionId left, DirectionTransitionId right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(DirectionTransitionId left, DirectionTransitionId right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(DirectionTransitionId left, DirectionTransitionId right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(DirectionTransitionId left, DirectionTransitionId right) =>
        left.CompareTo(right) >= 0;
}

[Obsolete("Use EasingId instead.")]
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

public sealed record DirectionTransitionSpec(
    DirectionTransitionId Kind,
    double DurationSeconds,
    EasingId Easing,
    bool Skippable = true)
{
    public DirectionTransitionId Id => Kind;

    [Obsolete("Use Easing instead.")]
    public DirectionEase Ease => ToDirectionEase(Easing);

    public DirectionTransitionSpec(
        DirectionTransitionId Kind,
        double DurationSeconds)
        : this(Kind, DurationSeconds, EasingId.Linear, true)
    {
    }

#pragma warning disable CS0618
    [Obsolete("Use constructor taking DirectionTransitionId instead.")]
    public DirectionTransitionSpec(
        DirectionTransitionKind Kind,
        double DurationSeconds)
        : this(DirectionTransitionId.FromKind(Kind), DurationSeconds, EasingId.Linear, true)
    {
    }

    public DirectionTransitionSpec(
        DirectionTransitionId Kind,
        double DurationSeconds,
        bool Skippable)
        : this(Kind, DurationSeconds, EasingId.Linear, Skippable)
    {
    }

    [Obsolete("Use constructor taking DirectionTransitionId instead.")]
    public DirectionTransitionSpec(
        DirectionTransitionKind kind,
        double durationSeconds,
        bool skippable)
        : this(DirectionTransitionId.FromKind(kind), durationSeconds, EasingId.Linear, skippable)
    {
    }

    [Obsolete("Use constructor taking EasingId instead.")]
    public DirectionTransitionSpec(
        DirectionTransitionId Kind,
        double DurationSeconds,
        DirectionEase Ease,
        bool Skippable = true)
        : this(Kind, DurationSeconds, FromDirectionEase(Ease), Skippable)
    {
    }

    [Obsolete("Use constructor taking DirectionTransitionId and EasingId instead.")]
    public DirectionTransitionSpec(
        DirectionTransitionKind kind,
        double durationSeconds,
        DirectionEase ease,
        bool skippable = true)
        : this(DirectionTransitionId.FromKind(kind), durationSeconds, FromDirectionEase(ease), skippable)
    {
    }

    [Obsolete("Use constructor taking DirectionTransitionId instead.")]
    public DirectionTransitionSpec(
        DirectionTransitionKind kind,
        double durationSeconds,
        EasingId easing,
        bool skippable = true)
        : this(DirectionTransitionId.FromKind(kind), durationSeconds, easing, skippable)
    {
    }
#pragma warning restore CS0618

    public static DirectionTransitionSpec Cut { get; } =
        new(DirectionTransitionId.Cut, 0, EasingId.Linear);

    public DirectionTransitionSpec Validate()
    {
        if (Kind.IsEmpty) throw new ArgumentException("Transition ID cannot be empty.");
        if (Easing.IsEmpty) throw new ArgumentException("Easing ID cannot be empty.");
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        if (Kind == DirectionTransitionId.Cut && DurationSeconds != 0)
            throw new ArgumentException("A cut transition cannot have a duration.");
        return this;
    }

#pragma warning disable CS0618
    [Obsolete("Use EasingId instead.")]
    public static EasingId FromDirectionEase(DirectionEase ease) => ease switch
    {
        DirectionEase.Linear => EasingId.Linear,
        DirectionEase.SineIn => EasingId.SineIn,
        DirectionEase.SineOut => EasingId.SineOut,
        DirectionEase.SineInOut => EasingId.SineInOut,
        DirectionEase.CubicIn => EasingId.CubicIn,
        DirectionEase.CubicOut => EasingId.CubicOut,
        DirectionEase.CubicInOut => EasingId.CubicInOut,
        DirectionEase.BackIn => EasingId.BackIn,
        DirectionEase.BackOut => EasingId.BackOut,
        DirectionEase.BackInOut => EasingId.BackInOut,
        _ => new(ease.ToString().ToLowerInvariant()),
    };

    [Obsolete("Use EasingId instead.")]
    public static DirectionEase ToDirectionEase(EasingId easing)
    {
        if (easing.Equals(EasingId.Linear)) return DirectionEase.Linear;
        if (easing.Equals(EasingId.SineIn)) return DirectionEase.SineIn;
        if (easing.Equals(EasingId.SineOut)) return DirectionEase.SineOut;
        if (easing.Equals(EasingId.SineInOut)) return DirectionEase.SineInOut;
        if (easing.Equals(EasingId.CubicIn)) return DirectionEase.CubicIn;
        if (easing.Equals(EasingId.CubicOut)) return DirectionEase.CubicOut;
        if (easing.Equals(EasingId.CubicInOut)) return DirectionEase.CubicInOut;
        if (easing.Equals(EasingId.BackIn)) return DirectionEase.BackIn;
        if (easing.Equals(EasingId.BackOut)) return DirectionEase.BackOut;
        if (easing.Equals(EasingId.BackInOut)) return DirectionEase.BackInOut;
        throw new DirectionException($"Easing '{easing}' cannot be represented by the legacy animation backend.");
    }
#pragma warning restore CS0618
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

public enum CurtainPosition
{
    Open,
    Closed,
}

public sealed record BackdropPatch(
    StageId StageId,
    BackdropState Backdrop,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record StageCreatePatch(
    StageId StageId,
    ViewId DefaultViewId,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record StageClearPatch(
    StageId StageId,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record StageReleasePatch(
    StageId StageId,
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

public sealed record ViewCreatePatch(
    ViewId ViewId,
    StageId StageId,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record ViewPosePatch(
    ViewId ViewId,
    DirectionValue<DirectionPoint> Center,
    DirectionValue<float> Zoom,
    DirectionValue<float> RotationDegrees,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record ViewReleasePatch(
    ViewId ViewId,
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

public sealed record CurtainPatch(
    CurtainState Curtain,
    DirectionTransitionSpec Transition,
    DirectionSourceLocation? SourceLocation = null) : IDurableDirectionPatch;

public sealed record CameraFxCue(
    ViewId ViewId,
    string EffectName,
    IReadOnlyDictionary<string, string> Parameters,
    double DurationSeconds,
    DirectionSourceLocation? SourceLocation = null) : ITransientDirectionCue;
