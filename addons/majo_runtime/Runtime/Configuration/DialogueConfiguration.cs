using System;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class DialogueConfiguration : Resource, IValidatableConfiguration
{
    public const string DefaultProjectIdValue = "main";
    public const string DefaultStartNodeValue = "Start";

    [Export]
    public string DefaultProjectId { get; set; } = DefaultProjectIdValue;

    [Export]
    public string DefaultStartNode { get; set; } = DefaultStartNodeValue;

    [Export]
    public string RequiredYarnPluginVersion { get; set; } =
        MajoRuntimeMetadata.RequiredYarnPluginVersion;

    [Export]
    public string RequiredYarnRuntimeVersion { get; set; } =
        MajoRuntimeMetadata.RequiredYarnRuntimeVersion;

    /// <summary>
    /// Stops the current dialogue for recoverable script/content command errors.
    /// Disabled by default so authoring mistakes can be corrected with hot reload.
    /// </summary>
    [Export]
    public bool AbortOnCommandError { get; set; }

    [Export]
    public YarnHotReloadConfiguration HotReload { get; set; } = new();

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(
            RequiredYarnPluginVersion,
            MajoRuntimeMetadata.RequiredYarnPluginVersion,
            StringComparison.Ordinal))
        {
            context.AddIssue(
                nameof(RequiredYarnPluginVersion),
                $"Version must be {MajoRuntimeMetadata.RequiredYarnPluginVersion}.");
        }

        if (!string.Equals(
            RequiredYarnRuntimeVersion,
            MajoRuntimeMetadata.RequiredYarnRuntimeVersion,
            StringComparison.Ordinal))
        {
            context.AddIssue(
                nameof(RequiredYarnRuntimeVersion),
                $"Version must be {MajoRuntimeMetadata.RequiredYarnRuntimeVersion}.");
        }

        if (string.IsNullOrWhiteSpace(DefaultProjectId))
        {
            context.AddIssue(
                nameof(DefaultProjectId),
                "Default project ID is required.");
        }

        if (string.IsNullOrWhiteSpace(DefaultStartNode))
        {
            context.AddIssue(
                nameof(DefaultStartNode),
                "Default start node is required.");
        }

        if (HotReload is null)
        {
            context.AddIssue(
                nameof(HotReload),
                "Configuration is required.");
            return;
        }

        HotReload.Validate(context.ForSection(nameof(HotReload)));
    }
}
