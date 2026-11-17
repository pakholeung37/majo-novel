#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using Majo.Runtime.Core;

namespace Majo.Runtime.Dialogue.Yarn;

public static partial class YarnDependencyValidator
{
    public const string RequiredPluginVersion = MajoRuntimeMetadata.RequiredYarnPluginVersion;
    public const string RequiredRuntimeVersion = MajoRuntimeMetadata.RequiredYarnRuntimeVersion;

    private const string YarnAddonDirectory = "YarnSpinner-Godot";
    private const string PluginManifestFile = "plugin.cfg";
    private const string PropsFile = "YarnSpinner-Godot.props";

    /// <summary>Checks the loaded runtime without relying on source files or a filesystem project root.</summary>
    public static YarnDependencyStatus ValidateRuntime() => ValidateRuntimeVersion(
        typeof(global::Yarn.Dialogue).Assembly.GetName().Version);

    public static YarnDependencyStatus ValidateRuntimeVersion(Version? version)
    {
        var detected = version is null ? null : $"{version.Major}.{version.Minor}.{version.Build}";
        var compatible = string.Equals(detected, RequiredRuntimeVersion, StringComparison.Ordinal);
        return new YarnDependencyStatus(compatible, null, detected, compatible
            ? $"Loaded Yarn Spinner {detected}; Godot adapter compatibility is checked by the runtime bridge."
            : $"Yarn Spinner {RequiredRuntimeVersion} is required, but loaded assembly version {version?.ToString() ?? "<unknown>"} was found.");
    }

    public static YarnDependencyStatus ValidateProject(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var addonRoot = Path.Combine(projectRoot, "addons", YarnAddonDirectory);
        var pluginManifestPath = Path.Combine(addonRoot, PluginManifestFile);
        var propsPath = Path.Combine(addonRoot, PropsFile);

        if (!File.Exists(pluginManifestPath))
        {
            return MissingFile(pluginManifestPath);
        }

        if (!File.Exists(propsPath))
        {
            return MissingFile(propsPath);
        }

        try
        {
            return ValidateContents(
                File.ReadAllText(pluginManifestPath),
                File.ReadAllText(propsPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new YarnDependencyStatus(
                false,
                null,
                null,
                $"Unable to read Yarn Spinner dependency metadata: {exception.Message}");
        }
    }

    public static YarnDependencyStatus ValidateContents(
        string pluginManifest,
        string propsContent)
    {
        ArgumentNullException.ThrowIfNull(pluginManifest);
        ArgumentNullException.ThrowIfNull(propsContent);

        var pluginVersionMatch = PluginVersionRegex().Match(pluginManifest);
        var runtimeVersionMatch = RuntimeVersionRegex().Match(propsContent);
        var pluginVersion = pluginVersionMatch.Success
            ? pluginVersionMatch.Groups["version"].Value
            : null;
        var runtimeVersion = runtimeVersionMatch.Success
            ? runtimeVersionMatch.Groups["version"].Value
            : null;

        if (pluginVersion is null || runtimeVersion is null)
        {
            return new YarnDependencyStatus(
                false,
                pluginVersion,
                runtimeVersion,
                "Yarn Spinner dependency metadata is malformed or incomplete.");
        }

        if (!string.Equals(pluginVersion, RequiredPluginVersion, StringComparison.Ordinal))
        {
            return new YarnDependencyStatus(
                false,
                pluginVersion,
                runtimeVersion,
                $"YarnSpinner-Godot {RequiredPluginVersion} is required, but {pluginVersion} was found.");
        }

        if (!string.Equals(runtimeVersion, RequiredRuntimeVersion, StringComparison.Ordinal))
        {
            return new YarnDependencyStatus(
                false,
                pluginVersion,
                runtimeVersion,
                $"Yarn Spinner {RequiredRuntimeVersion} is required, but {runtimeVersion} was found.");
        }

        return new YarnDependencyStatus(
            true,
            pluginVersion,
            runtimeVersion,
            $"Compatible Yarn Spinner dependency detected ({pluginVersion} / {runtimeVersion}).");
    }

    private static YarnDependencyStatus MissingFile(string path)
    {
        return new YarnDependencyStatus(
            false,
            null,
            null,
            $"Required Yarn Spinner dependency file was not found: {path}");
    }

    [GeneratedRegex(
        "^\\s*version\\s*=\\s*\"(?<version>[^\"]+)\"\\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PluginVersionRegex();

    [GeneratedRegex(
        "<PackageReference\\s+Include=\"YarnSpinner\"\\s+Version=\"(?<version>[^\"]+)\"\\s*/?>",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeVersionRegex();
}
