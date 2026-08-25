#nullable enable

namespace Majo.Runtime.Dialogue.Yarn;

public sealed class YarnDependencyStatus
{
    public static YarnDependencyStatus NotChecked { get; } = new(
        false,
        null,
        null,
        "Dependency check has not run.");

    public bool IsCompatible { get; }
    public string? DetectedPluginVersion { get; }
    public string? DetectedRuntimeVersion { get; }
    public string Message { get; }

    public YarnDependencyStatus(
        bool isCompatible,
        string? detectedPluginVersion,
        string? detectedRuntimeVersion,
        string message)
    {
        IsCompatible = isCompatible;
        DetectedPluginVersion = detectedPluginVersion;
        DetectedRuntimeVersion = detectedRuntimeVersion;
        Message = message;
    }
}
