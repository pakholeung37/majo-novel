#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Stage;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class MotionCurveConverter : ICommandValueConverter<MotionCurve>, ICommandValueDescriptor<MotionCurve>
{
    public static MotionCurveConverter Instance { get; } = new();

    private static readonly string[] MotionCurveExamples = ["linear", "cubic-out", "back-out"];

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "MotionCurve",
        CanonicalValues: Enum.GetNames<MotionCurve>().Select(name => name.ToLowerInvariant()).ToArray(),
        Examples: MotionCurveExamples);

    public CommandValueDescription? Description => DefaultDescription;

    public MotionCurve Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (Enum.TryParse<MotionCurve>(
                text.Replace("-", string.Empty, StringComparison.Ordinal),
                ignoreCase: true,
                out var result))
        {
            return result;
        }

        throw new CommandValueFormatException(
            "must be a supported motion curve such as linear, cubic-out, or back-out");
    }

    public string Format(MotionCurve value) =>
        value.ToString().ToLowerInvariant();
}

public sealed class DirectionEaseConverter : ICommandValueConverter<DirectionEase>, ICommandValueDescriptor<DirectionEase>
{
    private static readonly string[] DirectionEaseExamples = ["linear", "sine-in", "cubic-out", "back-out"];

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "DirectionEase",
        Syntax: "<easing-id>",
        Examples: DirectionEaseExamples);

    private readonly IEasingRegistry easingRegistry;

    public DirectionEaseConverter(IEasingRegistry easingRegistry)
    {
        this.easingRegistry = easingRegistry ?? throw new ArgumentNullException(nameof(easingRegistry));
    }

    public CommandValueDescription? Description => DefaultDescription;

    public DirectionEase Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        try
        {
            var def = easingRegistry.Resolve(text.Trim());
            return DirectionTransitionSpec.ToDirectionEase(def.Id);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            throw new CommandValueFormatException(
                $"must be a valid DirectionEase (got '{text}'): {ex.Message}",
                ex);
        }
    }

    public string Format(DirectionEase value) =>
        value.ToString().ToLowerInvariant();
}

public sealed class DirectionTransitionIdConverter : ICommandValueConverter<DirectionTransitionId>, ICommandValueDescriptor<DirectionTransitionId>
{
    public static DirectionTransitionIdConverter Instance { get; } = new();

    private static readonly string[] DirectionTransitionExamples = ["cut", "dissolve", "fade", "move"];

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "DirectionTransitionId",
        Syntax: "<kind>",
        Examples: DirectionTransitionExamples);

    public CommandValueDescription? Description => DefaultDescription;

    public DirectionTransitionId Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new DirectionTransitionId(text.Trim());
    }

    public string Format(DirectionTransitionId value) =>
        value.Value;
}
