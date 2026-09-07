#nullable enable

using System;
using System.Collections.Generic;
using Majo.Runtime.Stage;

namespace Majo.Runtime.Dialogue.Commands;

public sealed class CameraShakeAxisConverter : ICommandValueConverter<CameraShakeAxis>, ICommandValueDescriptor<CameraShakeAxis>
{
    public static CameraShakeAxisConverter Instance { get; } = new();

    private static readonly string[] CanonicalAxisValues = ["all", "x", "y", "diagonal"];
    private static readonly string[] AxisExamples = ["all", "x", "y", "diagonal"];

    private static readonly CommandValueDescription DefaultDescription = new(
        DisplayName: "CameraShakeAxis",
        CanonicalValues: CanonicalAxisValues,
        Aliases: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["both"] = "all",
            ["diag"] = "diagonal",
        },
        Examples: AxisExamples);

    public CommandValueDescription? Description => DefaultDescription;

    public CameraShakeAxis Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text.Trim().ToLowerInvariant() switch
        {
            "all" or "both" => CameraShakeAxis.All,
            "x" => CameraShakeAxis.X,
            "y" => CameraShakeAxis.Y,
            "diagonal" or "diag" => CameraShakeAxis.Diagonal,
            _ => throw new CommandValueFormatException("must be 'all', 'x', 'y', or 'diagonal'"),
        };
    }

    public string Format(CameraShakeAxis value) =>
        value.ToString().ToLowerInvariant();
}
