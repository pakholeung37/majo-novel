#nullable enable

using System;
using System.Globalization;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class StringCommandValueConverter : ICommandValueConverter<string>, ICommandValueDescriptor<string>
{
    public static StringCommandValueConverter Instance { get; } = new();

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "string",
        Syntax: "<string>");

    public CommandValueDescription? Description => DefaultDescription;

    public string Parse(string text) => text;

    public string Format(string value) => value;
}

public sealed class BooleanCommandValueConverter : ICommandValueConverter<bool>, ICommandValueDescriptor<bool>
{
    public static BooleanCommandValueConverter Instance { get; } = new();

    private static readonly string[] BooleanCanonicalValues = ["true", "false"];

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "bool",
        Syntax: "<bool>",
        CanonicalValues: BooleanCanonicalValues);

    public CommandValueDescription? Description => DefaultDescription;

    public bool Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();
        if (bool.TryParse(trimmed, out var result))
        {
            return result;
        }

        throw new CommandValueFormatException("must be true or false");
    }

    public string Format(bool value) => value ? "true" : "false";
}

public sealed class Int32CommandValueConverter : ICommandValueConverter<int>, ICommandValueDescriptor<int>
{
    public static Int32CommandValueConverter Instance { get; } = new();

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "int",
        Syntax: "<integer>");

    public CommandValueDescription? Description => DefaultDescription;

    public int Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new CommandValueFormatException("must be an integer");
    }

    public string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed class DoubleCommandValueConverter : ICommandValueConverter<double>, ICommandValueDescriptor<double>
{
    public static DoubleCommandValueConverter Instance { get; } = new();

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "double",
        Syntax: "<number>");

    public CommandValueDescription? Description => DefaultDescription;

    public double Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) &&
            double.IsFinite(result))
        {
            return result;
        }

        throw new CommandValueFormatException("must be a finite number");
    }

    public string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed class SingleCommandValueConverter : ICommandValueConverter<float>, ICommandValueDescriptor<float>
{
    public static SingleCommandValueConverter Instance { get; } = new();

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "float",
        Syntax: "<number>");

    public CommandValueDescription? Description => DefaultDescription;

    public float Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) &&
            float.IsFinite(result))
        {
            return result;
        }

        throw new CommandValueFormatException("must be a finite number");
    }

    public string Format(float value) => value.ToString(CultureInfo.InvariantCulture);
}
