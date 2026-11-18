#nullable enable

using System;
using System.Globalization;
using Godot;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class Vector2Converter : ICommandValueConverter<Vector2>, ICommandValueDescriptor<Vector2>
{
    public static Vector2Converter Instance { get; } = new();

    private static readonly string[] Vector2Examples = ["0,0", "12,-4", "80,-35"];

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "Vector2",
        Syntax: "<x,y>",
        Examples: Vector2Examples);

    public CommandValueDescription? Description => DefaultDescription;

    public Vector2 Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var components = text.Split(',', StringSplitOptions.TrimEntries);
        if (components.Length != 2 ||
            !float.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.IsFinite(x) ||
            !float.IsFinite(y))
        {
            throw new CommandValueFormatException("must be a finite x,y vector");
        }

        return new Vector2(x, y);
    }

    public string Format(Vector2 value) =>
        $"{value.X.ToString(CultureInfo.InvariantCulture)},{value.Y.ToString(CultureInfo.InvariantCulture)}";
}
