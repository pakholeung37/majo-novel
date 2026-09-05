#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CommandValueConverterRegistry
{
    private readonly Dictionary<Type, Func<string, object>> converters = new();
    private bool isSealed;

    public CommandValueConverterRegistry()
    {
        Register<string>(value => value);
        Register<bool>(value => bool.TryParse(value, out var result)
            ? result
            : throw new FormatException("must be true or false"));
        Register<double>(value =>
            double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result) &&
            double.IsFinite(result)
                ? result
                : throw new FormatException("must be a finite number"));
        Register<int>(value =>
            int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result)
                ? result
                : throw new FormatException("must be an integer"));
        Register<Vector2>(ParseVector2);
        Register<MotionCurve>(value => Enum.TryParse<MotionCurve>(
                value.Replace("-", string.Empty, StringComparison.Ordinal),
                ignoreCase: true,
                out var result)
            ? result
            : throw new FormatException(
                "must be a supported motion curve such as linear, cubic-out, or back-out"));
        Register<CameraShakeAxis>(value => value.Trim().ToLowerInvariant() switch
        {
            "all" or "both" => CameraShakeAxis.All,
            "x" => CameraShakeAxis.X,
            "y" => CameraShakeAxis.Y,
            "diagonal" or "diag" => CameraShakeAxis.Diagonal,
            _ => throw new FormatException("must be 'all', 'x', 'y', or 'diagonal'"),
        });
        Register<float>(value =>
            float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result) &&
            float.IsFinite(result)
                ? result
                : throw new FormatException("must be a finite number"));
        Register<DirectionEase>(value => value.Trim().ToLowerInvariant() switch
        {
            "linear" => DirectionEase.Linear,
            "sine-in" or "sinein" => DirectionEase.SineIn,
            "sine-out" or "sineout" => DirectionEase.SineOut,
            "sine-in-out" or "sineinout" => DirectionEase.SineInOut,
            "cubic-in" or "cubicin" => DirectionEase.CubicIn,
            "cubic-out" or "cubicout" => DirectionEase.CubicOut,
            "cubic-in-out" or "cubicinout" => DirectionEase.CubicInOut,
            "back-out" or "backout" => DirectionEase.BackOut,
            "back-in" or "backin" => DirectionEase.BackIn,
            "back-in-out" or "backinout" => DirectionEase.BackInOut,
            _ => throw new FormatException($"must be a valid DirectionEase (got '{value}')"),
        });
        Register<DirectionTransitionId>(value => new DirectionTransitionId(value));
    }

    public void Seal() => isSealed = true;

    public CommandValueConverterRegistry Register<TValue>(Func<string, TValue> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        if (isSealed)
        {
            throw new InvalidOperationException("Converters cannot be registered after catalog is sealed.");
        }
        converters[typeof(TValue)] = value => converter(value)!;
        return this;
    }

    public object Convert(string value, Type valueType)
    {
        var targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (converters.TryGetValue(targetType, out var converter))
        {
            return converter(value);
        }

        if (targetType.IsEnum)
        {
            try
            {
                return Enum.Parse(targetType, value, ignoreCase: true);
            }
            catch (ArgumentException)
            {
                var values = string.Join(
                    ", ",
                    Enum.GetNames(targetType).Select(name => name.ToLowerInvariant()));
                throw new FormatException($"must be one of {values}");
            }
        }

        throw new InvalidOperationException(
            $"No command value converter is registered for '{targetType.FullName}'.");
    }

    public TValue Convert<TValue>(string value) =>
        (TValue)Convert(value, typeof(TValue));

    public bool CanConvert(Type valueType)
    {
        var targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        return targetType.IsEnum || converters.ContainsKey(targetType);
    }

    private static Vector2 ParseVector2(string value)
    {
        var components = value.Split(',', StringSplitOptions.TrimEntries);
        if (components.Length != 2 ||
            !float.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.IsFinite(x) || !float.IsFinite(y))
        {
            throw new FormatException("must be a finite x,y vector");
        }

        return new Vector2(x, y);
    }
}
