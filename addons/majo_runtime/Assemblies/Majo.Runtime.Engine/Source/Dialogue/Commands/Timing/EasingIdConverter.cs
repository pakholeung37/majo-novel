#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class EasingIdConverter : ICommandValueConverter<EasingId>, ICommandValueDescriptor<EasingId>
{
    private static readonly string[] EasingIdExamples = ["linear", "sine-in", "cubic-out", "back-out"];

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "EasingId",
        Syntax: "<easing-id>",
        Examples: EasingIdExamples);

    private readonly IEasingRegistry easingRegistry;

    public EasingIdConverter(IEasingRegistry easingRegistry)
    {
        this.easingRegistry = easingRegistry ?? throw new ArgumentNullException(nameof(easingRegistry));
    }

    public CommandValueDescription? Description => DefaultDescription;

    public EasingId Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        try
        {
            var def = easingRegistry.Resolve(text.Trim());
            return def.Id;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            throw new CommandValueFormatException(
                $"must be a valid registered easing (got '{text}'): {ex.Message}",
                ex);
        }
    }

    public string Format(EasingId value) =>
        value.Value;
}
