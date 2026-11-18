#nullable enable

using System;
using Majo.Runtime.Core;

namespace Majo.Runtime.Direction.Core;

public class DirectionException : MajoRuntimeException
{
    public DirectionException(string message)
        : base(message)
    {
    }

    public DirectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DirectionConflictException : DirectionException
{
    public DirectionConflictException(
        string stablePath,
        DirectionSourceLocation? first,
        DirectionSourceLocation? second)
        : base(CreateMessage(stablePath, first, second))
    {
        StablePath = stablePath;
        First = first;
        Second = second;
    }

    public string StablePath { get; }
    public DirectionSourceLocation? First { get; }
    public DirectionSourceLocation? Second { get; }

    private static string CreateMessage(
        string stablePath,
        DirectionSourceLocation? first,
        DirectionSourceLocation? second) =>
        $"Direction path '{stablePath}' is written more than once in the same beat" +
        $" ({first?.ToString() ?? "unknown"}, {second?.ToString() ?? "unknown"}).";
}

public sealed class DirectionReferenceException : DirectionException
{
    public DirectionReferenceException(string message)
        : base(message)
    {
    }
}

public sealed class DirectionRevisionException : DirectionException
{
    public DirectionRevisionException(long expected, long actual)
        : base($"Direction revision changed: expected {expected}, actual {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public long Expected { get; }
    public long Actual { get; }
}

public sealed class DirectionBeatException : DirectionException
{
    public DirectionBeatException(string message)
        : base(message)
    {
    }
}
