#nullable enable

using System;

namespace Majo.Runtime.Direction.Core;

public readonly record struct StageId
{
    public StageId(string value) => Value = DirectionIdentifier.Validate(value, nameof(value));

    public string Value { get; }

    public static StageId Main { get; } = new("main");

    public override string ToString() => Value;
}

public readonly record struct ViewId
{
    public ViewId(string value) => Value = DirectionIdentifier.Validate(value, nameof(value));

    public string Value { get; }

    public static ViewId Main { get; } = new("main");

    public override string ToString() => Value;
}

public readonly record struct ActorId
{
    public ActorId(string value) => Value = DirectionIdentifier.Validate(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ActorAddress(StageId StageId, ActorId ActorId)
{
    public override string ToString() => $"{StageId}/{ActorId}";
}

public readonly record struct LayoutId
{
    public LayoutId(string value) => Value = DirectionIdentifier.Validate(value, nameof(value));

    public string Value { get; }

    public static LayoutId SingleLayout { get; } = new("single");

    public override string ToString() => Value;
}

public readonly record struct LayoutSlotId
{
    public LayoutSlotId(string value) => Value = DirectionIdentifier.Validate(value, nameof(value));

    public string Value { get; }

    public static LayoutSlotId Main { get; } = new("main");

    public override string ToString() => Value;
}

public readonly record struct DirectionTransactionId(Guid Value)
{
    public static DirectionTransactionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

internal static class DirectionIdentifier
{
    public static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Contains('\0'))
        {
            throw new ArgumentException(
                "Direction identifiers cannot contain null characters.", parameterName);
        }

        return normalized;
    }
}
