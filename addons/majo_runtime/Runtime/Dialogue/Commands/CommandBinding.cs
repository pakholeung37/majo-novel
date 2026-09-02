#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Majo.Runtime.Configuration;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed record CommandBindingContext(
    double DefaultAnimationDurationSeconds,
    double DefaultAudioFadeDurationSeconds = RuntimeDefaults.AnimationDurationSeconds,
    double DefaultCharacterAnimationDurationSeconds = RuntimeDefaults.CommandBindingCharacterAnimationDurationSeconds,
    DirectionTransitionKind DefaultCharacterAnimationKind = DirectionTransitionKind.Fade)
{
    public CommandBindingContext Validate()
    {
        if (!double.IsFinite(DefaultAnimationDurationSeconds) ||
            DefaultAnimationDurationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultAnimationDurationSeconds),
                "Default animation duration must be finite and non-negative.");
        }

        if (!double.IsFinite(DefaultAudioFadeDurationSeconds) ||
            DefaultAudioFadeDurationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultAudioFadeDurationSeconds),
                "Default audio fade duration must be finite and non-negative.");
        }

        if (!double.IsFinite(DefaultCharacterAnimationDurationSeconds) ||
            DefaultCharacterAnimationDurationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultCharacterAnimationDurationSeconds),
                "Default character animation duration must be finite and non-negative.");
        }

        if (!Enum.IsDefined(DefaultCharacterAnimationKind) ||
            DefaultCharacterAnimationKind == DirectionTransitionKind.Move)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultCharacterAnimationKind),
                "Default character animation kind must be cut, fade, or dissolve.");
        }

        return this;
    }
}

public sealed class CommandValueConverterRegistry
{
    private readonly Dictionary<Type, Func<string, object>> converters = new();

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
    }

    public CommandValueConverterRegistry Register<TValue>(Func<string, TValue> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        converters[typeof(TValue)] = value => converter(value)!;
        return this;
    }

    internal object Convert(string value, Type valueType)
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

    internal bool CanConvert(Type valueType)
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

public sealed class CommandBinder
{
    private readonly CommandBindingContext context;
    private readonly CommandValueConverterRegistry converters;

    public CommandBinder(
        CommandBindingContext context,
        CommandValueConverterRegistry? converters = null)
    {
        this.context = (context ?? throw new ArgumentNullException(nameof(context))).Validate();
        this.converters = converters ?? new CommandValueConverterRegistry();
    }

    public CommandBindingContext Context => context;

    public TArguments Bind<TArguments>(
        CommandDescriptor descriptor,
        IReadOnlyList<string> arguments)
        where TArguments : class, new()
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(arguments);
        if (descriptor.ArgumentsType != typeof(TArguments))
        {
            throw new ArgumentException(
                $"Descriptor for '{descriptor.ArgumentsType.FullName}' cannot bind " +
                $"'{typeof(TArguments).FullName}'.",
                nameof(descriptor));
        }

        var raw = ParseRaw(descriptor, arguments);
        ValidateParameterSets(descriptor, raw.AssignedNames);
        var result = new TArguments();
        foreach (var parameter in descriptor.Parameters)
        {
            if (!TryGetRawValue(parameter, raw, out var value))
            {
                if (parameter.DefaultSource == CommandDefaultSource.AnimationDuration)
                {
                    value = context.DefaultAnimationDurationSeconds.ToString(
                        "R",
                        CultureInfo.InvariantCulture);
                }
                else if (parameter.DefaultSource ==
                    CommandDefaultSource.CharacterAnimationDuration)
                {
                    value = context.DefaultCharacterAnimationDurationSeconds.ToString(
                        "R",
                        CultureInfo.InvariantCulture);
                }
                else if (parameter.DefaultSource == CommandDefaultSource.AudioFadeDuration)
                {
                    value = context.DefaultAudioFadeDurationSeconds.ToString(
                        "R",
                        CultureInfo.InvariantCulture);
                }
                else if (parameter.DefaultValue is not null)
                {
                    value = parameter.DefaultValue;
                }
                else if (parameter.Required)
                {
                    throw Invalid(
                        descriptor,
                        parameter.Primary
                            ? $"expected primary parameter '{parameter.Name}'"
                            : $"parameter '{parameter.Name}' is required");
                }
                else
                {
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(value) || value == "-")
            {
                throw Invalid(descriptor, $"parameter '{parameter.Name}' requires a value");
            }

            object converted;
            try
            {
                converted = converters.Convert(value, parameter.ValueType);
            }
            catch (FormatException exception)
            {
                throw Invalid(
                    descriptor,
                    $"{parameter.Name} '{value}' {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                throw new RuntimeCommandException(descriptor.Name, exception.Message, exception);
            }

            if (!double.IsNaN(parameter.Minimum) &&
                converted is IConvertible convertible &&
                convertible.ToDouble(CultureInfo.InvariantCulture) < parameter.Minimum)
            {
                throw Invalid(
                    descriptor,
                    $"{parameter.Name} '{value}' must be at least " +
                    parameter.Minimum.ToString(CultureInfo.InvariantCulture));
            }

            parameter.Property.SetValue(result, converted);
        }

        return result;
    }

    public void Validate(CommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        foreach (var parameter in descriptor.Parameters)
        {
            if (!converters.CanConvert(parameter.ValueType))
            {
                throw new RuntimeCommandException(
                    descriptor.Name,
                    $"No command value converter is registered for " +
                    $"'{parameter.ValueType.FullName}' parameter '{parameter.Name}'.");
            }

            if (parameter.DefaultValue is not null)
            {
                try
                {
                    _ = converters.Convert(parameter.DefaultValue, parameter.ValueType);
                }
                catch (FormatException exception)
                {
                    throw new RuntimeCommandException(
                        descriptor.Name,
                        $"Default for parameter '{parameter.Name}' {exception.Message}.",
                        exception);
                }
            }

            if (parameter.DefaultSource is CommandDefaultSource.AnimationDuration or
                    CommandDefaultSource.CharacterAnimationDuration or
                    CommandDefaultSource.AudioFadeDuration &&
                parameter.ValueType != typeof(double))
            {
                throw new RuntimeCommandException(
                    descriptor.Name,
                    $"Parameter '{parameter.Name}' uses a duration default " +
                    "but is not a double.");
            }

            var numericType = Nullable.GetUnderlyingType(parameter.ValueType) ??
                parameter.ValueType;
            if (!double.IsNaN(parameter.Minimum) &&
                !typeof(IConvertible).IsAssignableFrom(numericType))
            {
                throw new RuntimeCommandException(
                    descriptor.Name,
                    $"Parameter '{parameter.Name}' declares a minimum but its type " +
                    $"'{parameter.ValueType.FullName}' is not numeric.");
            }
        }
    }

    private static RawArguments ParseRaw(
        CommandDescriptor descriptor,
        IReadOnlyList<string> arguments)
    {
        var primaryDescriptor = descriptor.Parameters.SingleOrDefault(parameter => parameter.Primary);
        string? primary = null;
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            var separator = argument.IndexOf(':');
            if (separator < 0)
            {
                if (primaryDescriptor is null || primary is not null)
                {
                    throw Invalid(
                        descriptor,
                        $"unexpected positional argument '{argument}'");
                }

                primary = argument;
                continue;
            }

            var name = argument[..separator];
            var value = argument[(separator + 1)..];
            var parameter = descriptor.Parameters.FirstOrDefault(candidate =>
                !candidate.Primary &&
                candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (parameter is null)
            {
                throw Invalid(descriptor, $"unknown parameter '{name}'");
            }

            if (!named.TryAdd(parameter.Name, value))
            {
                throw Invalid(descriptor, $"parameter '{name}' is duplicated");
            }
        }

        return new RawArguments(primary, named);
    }

    private static void ValidateParameterSets(
        CommandDescriptor descriptor,
        IReadOnlySet<string> assignedNames)
    {
        foreach (var set in descriptor.ParameterSets)
        {
            var count = set.Parameters.Count(assignedNames.Contains);
            if (set.Rule is (CommandParameterSetRule.AtMostOne or
                    CommandParameterSetRule.ExactlyOne) && count > 1)
            {
                throw Invalid(
                    descriptor,
                    $"{string.Join(" and ", set.Parameters)} are mutually exclusive");
            }

            if (set.Rule == CommandParameterSetRule.ExactlyOne && count != 1)
            {
                throw Invalid(
                    descriptor,
                    $"either {string.Join(" or ", set.Parameters)} is required");
            }

            if (set.Rule == CommandParameterSetRule.AtLeastOne && count == 0)
            {
                throw Invalid(
                    descriptor,
                    $"at least one of {string.Join(", ", set.Parameters)} is required");
            }
        }
    }

    private static bool TryGetRawValue(
        CommandParameterDescriptor parameter,
        RawArguments arguments,
        out string value)
    {
        if (parameter.Primary)
        {
            value = arguments.Primary ?? string.Empty;
            return arguments.Primary is not null;
        }

        return arguments.Named.TryGetValue(parameter.Name, out value!);
    }

    private static RuntimeCommandException Invalid(
        CommandDescriptor descriptor,
        string message) => new(
            descriptor.Name,
            $"{message}. Usage: {descriptor.Usage}.");

    private sealed class RawArguments
    {
        public RawArguments(string? primary, IReadOnlyDictionary<string, string> named)
        {
            Primary = primary;
            Named = named;
            AssignedNames = new HashSet<string>(named.Keys, StringComparer.OrdinalIgnoreCase);
            if (primary is not null)
            {
                AssignedNames.Add(string.Empty);
            }
        }

        public string? Primary { get; }

        public IReadOnlyDictionary<string, string> Named { get; }

        public HashSet<string> AssignedNames { get; }
    }
}
