#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Stage;

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

    public static bool operator <(DirectionTransitionId left, DirectionTransitionId right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(DirectionTransitionId left, DirectionTransitionId right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(DirectionTransitionId left, DirectionTransitionId right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(DirectionTransitionId left, DirectionTransitionId right) =>
        left.CompareTo(right) >= 0;
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
    DirectionTransitionId Kind,
    double DurationSeconds,
    DirectionEase Ease = DirectionEase.Linear,
    bool Skippable = true)
{
    public DirectionTransitionId Id => Kind;

    public DirectionTransitionSpec(
        DirectionTransitionKind kind,
        double durationSeconds,
        DirectionEase ease = DirectionEase.Linear,
        bool skippable = true)
        : this(DirectionTransitionId.FromKind(kind), durationSeconds, ease, skippable)
    {
    }

    public static DirectionTransitionSpec Cut { get; } =
        new(DirectionTransitionId.Cut, 0);

    public DirectionTransitionSpec Validate()
    {
        if (Kind.IsEmpty) throw new ArgumentException("Transition ID cannot be empty.");
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
