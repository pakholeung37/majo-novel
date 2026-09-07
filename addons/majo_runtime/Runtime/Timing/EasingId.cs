#nullable enable

using System;

namespace Majo.Runtime.Timing;

public readonly record struct EasingId : IEquatable<EasingId>, IComparable<EasingId>
{
    public string Value { get; }

    public EasingId(string? value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    public static EasingId Linear { get; } = new("linear");
    public static EasingId SineIn { get; } = new("sine-in");
    public static EasingId SineOut { get; } = new("sine-out");
    public static EasingId SineInOut { get; } = new("sine-in-out");
    public static EasingId CubicIn { get; } = new("cubic-in");
    public static EasingId CubicOut { get; } = new("cubic-out");
    public static EasingId CubicInOut { get; } = new("cubic-in-out");
    public static EasingId BackIn { get; } = new("back-in");
    public static EasingId BackOut { get; } = new("back-out");
    public static EasingId BackInOut { get; } = new("back-in-out");

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public bool Equals(EasingId other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);

    public int CompareTo(EasingId other) =>
        string.Compare(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public static bool operator <(EasingId left, EasingId right) => left.CompareTo(right) < 0;
    public static bool operator <=(EasingId left, EasingId right) => left.CompareTo(right) <= 0;
    public static bool operator >(EasingId left, EasingId right) => left.CompareTo(right) > 0;
    public static bool operator >=(EasingId left, EasingId right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator EasingId(string? value) => new(value);
}
