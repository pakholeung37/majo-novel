#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Timing;

namespace Majo.Runtime.Dialogue.Commands;

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
