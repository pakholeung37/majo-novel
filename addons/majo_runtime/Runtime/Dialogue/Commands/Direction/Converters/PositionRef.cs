#nullable enable

using System;
using System.Globalization;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Dialogue.Commands;

/// <summary>A coordinate literal or an unbound name, resolved in the executing node.</summary>
public abstract record PositionRef
{
    private PositionRef() { }
    public sealed record Literal(DirectionPoint Point) : PositionRef;
    public sealed record Named(string Name) : PositionRef;
}

public sealed class PositionRefConverter : ICommandValueConverter<PositionRef>, ICommandValueDescriptor<PositionRef>
{
    public static PositionRefConverter Instance { get; } = new();
    public CommandValueDescription? Description { get; } = new(
        DisplayName: "Position", Syntax: "<mark|x,y>", Examples: ["left", "witness", "30,0"]);

    public string Format(PositionRef value) => value switch
    {
        PositionRef.Named named => named.Name,
        PositionRef.Literal literal => string.Create(CultureInfo.InvariantCulture,
            $"{literal.Point.X},{literal.Point.Y}"),
        _ => throw new ArgumentException("Unknown position reference.", nameof(value)),
    };

    public PositionRef Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new CommandValueFormatException("Position requires a mark name or x,y percentages.");
        var value = text.Trim();
        if (!value.Contains(',')) return new PositionRef.Named(value);
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.IsFinite(x) || !float.IsFinite(y) ||
            x is < 0 or > 100 || y is < 0 or > 100)
            throw new CommandValueFormatException(
                $"Invalid position '{text}'. Use finite x,y percentages between 0 and 100.");
        return new PositionRef.Literal(new DirectionPoint(x, y));
    }
}
