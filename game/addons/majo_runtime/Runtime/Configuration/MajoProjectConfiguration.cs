#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Core;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class MajoProjectConfiguration : Resource
{
    [Export]
    public RuntimeConfiguration? Runtime { get; set; }

    [Export]
    public OperationConfiguration? Operations { get; set; }

    [Export]
    public ActorConfiguration? Actors { get; set; }

    [Export]
    public DialogueConfiguration? Dialogue { get; set; }

    public static MajoProjectConfiguration CreateDefault() => new()
    {
        Runtime = new RuntimeConfiguration(),
        Operations = new OperationConfiguration(),
        Actors = new ActorConfiguration(),
        Dialogue = new DialogueConfiguration(),
    };

    public ConfigurationValidationResult Validate(Func<string, bool>? resourceExists = null)
    {
        resourceExists ??= path => ResourceLoader.Exists(path);
        var issues = new List<ConfigurationIssue>();

        ValidateRuntime(issues);
        ValidateOperations(issues);
        ValidateActors(issues, resourceExists);
        ValidateDialogue(issues);

        return new ConfigurationValidationResult(issues);
    }

    private void ValidateRuntime(ICollection<ConfigurationIssue> issues)
    {
        if (Runtime is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Runtime), "Configuration is required."));
            return;
        }

        if (!double.IsFinite(Runtime.LifecycleTimeoutSeconds) ||
            Runtime.LifecycleTimeoutSeconds <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Runtime)}.{nameof(Runtime.LifecycleTimeoutSeconds)}",
                "Value must be finite and greater than zero."));
        }
    }

    private void ValidateOperations(ICollection<ConfigurationIssue> issues)
    {
        if (Operations is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Operations), "Configuration is required."));
            return;
        }

        if (!double.IsFinite(Operations.DefaultAnimationDurationSeconds) ||
            Operations.DefaultAnimationDurationSeconds <= 0)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Operations)}.{nameof(Operations.DefaultAnimationDurationSeconds)}",
                "Value must be finite and greater than zero."));
        }
    }

    private void ValidateActors(
        ICollection<ConfigurationIssue> issues,
        Func<string, bool> resourceExists)
    {
        if (Actors is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Actors), "Configuration is required."));
            return;
        }

        var actorIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Actors.Actors.Count; index++)
        {
            var actor = Actors.Actors[index];
            var path = $"{nameof(Actors)}.{nameof(ActorConfiguration.Actors)}[{index}]";
            if (actor is null)
            {
                issues.Add(new ConfigurationIssue(path, "Actor definition is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(actor.Id))
            {
                issues.Add(new ConfigurationIssue($"{path}.{nameof(actor.Id)}", "ID is required."));
            }
            else if (!actorIds.Add(actor.Id))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.Id)}",
                    $"Actor ID '{actor.Id}' is duplicated."));
            }

            if (string.IsNullOrWhiteSpace(actor.AssetPath))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.AssetPath)}",
                    "Resource path is required."));
            }
            else if (!resourceExists(actor.AssetPath))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.AssetPath)}",
                    $"Resource '{actor.AssetPath}' does not exist."));
            }
        }
    }

    private void ValidateDialogue(ICollection<ConfigurationIssue> issues)
    {
        if (Dialogue is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Dialogue), "Configuration is required."));
            return;
        }

        if (!string.Equals(
            Dialogue.RequiredYarnPluginVersion,
            MajoRuntimeMetadata.RequiredYarnPluginVersion,
            StringComparison.Ordinal))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Dialogue)}.{nameof(Dialogue.RequiredYarnPluginVersion)}",
                $"Version must be {MajoRuntimeMetadata.RequiredYarnPluginVersion}."));
        }

        if (!string.Equals(
            Dialogue.RequiredYarnRuntimeVersion,
            MajoRuntimeMetadata.RequiredYarnRuntimeVersion,
            StringComparison.Ordinal))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Dialogue)}.{nameof(Dialogue.RequiredYarnRuntimeVersion)}",
                $"Version must be {MajoRuntimeMetadata.RequiredYarnRuntimeVersion}."));
        }
    }
}
