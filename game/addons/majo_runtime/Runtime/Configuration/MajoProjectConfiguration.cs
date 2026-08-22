#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;
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
        ValidatePosition(Actors.LeftPosition, nameof(Actors.LeftPosition), issues);
        ValidatePosition(Actors.CenterPosition, nameof(Actors.CenterPosition), issues);
        ValidatePosition(Actors.RightPosition, nameof(Actors.RightPosition), issues);
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

            if (string.IsNullOrWhiteSpace(actor.FactoryId))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.FactoryId)}",
                    "Factory ID is required."));
            }

            var builtInTextureFactory = actor.FactoryId is ActorFactoryIds.Character or
                ActorFactoryIds.Background;
            if (builtInTextureFactory && actor.FactoryConfiguration is not null)
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.FactoryConfiguration)}",
                    $"Built-in factory '{actor.FactoryId}' uses the texture appearance fields."));
            }

            var usesTextureDefinition = builtInTextureFactory ||
                (actor.FactoryConfiguration is null && actor.Appearances.Count > 0);
            if (!usesTextureDefinition)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(actor.DefaultAppearance))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.DefaultAppearance)}",
                    "Default appearance is required."));
            }

            var appearanceIds = new HashSet<string>(StringComparer.Ordinal);
            for (var appearanceIndex = 0;
                appearanceIndex < actor.Appearances.Count;
                appearanceIndex++)
            {
                var appearance = actor.Appearances[appearanceIndex];
                var appearancePath =
                    $"{path}.{nameof(actor.Appearances)}[{appearanceIndex}]";
                if (appearance is null)
                {
                    issues.Add(new ConfigurationIssue(
                        appearancePath, "Appearance definition is required."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(appearance.Id))
                {
                    issues.Add(new ConfigurationIssue(
                        $"{appearancePath}.{nameof(appearance.Id)}",
                        "Appearance ID is required."));
                }
                else if (!appearanceIds.Add(appearance.Id))
                {
                    issues.Add(new ConfigurationIssue(
                        $"{appearancePath}.{nameof(appearance.Id)}",
                        $"Appearance ID '{appearance.Id}' is duplicated."));
                }

                if (string.IsNullOrWhiteSpace(appearance.TexturePath))
                {
                    issues.Add(new ConfigurationIssue(
                        $"{appearancePath}.{nameof(appearance.TexturePath)}",
                        "Texture path is required."));
                }
                else if (!resourceExists(appearance.TexturePath))
                {
                    issues.Add(new ConfigurationIssue(
                        $"{appearancePath}.{nameof(appearance.TexturePath)}",
                        $"Resource '{appearance.TexturePath}' does not exist."));
                }
            }

            if (actor.Appearances.Count == 0)
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.Appearances)}",
                    "At least one appearance is required."));
            }
            else if (!string.IsNullOrWhiteSpace(actor.DefaultAppearance) &&
                !appearanceIds.Contains(actor.DefaultAppearance))
            {
                issues.Add(new ConfigurationIssue(
                    $"{path}.{nameof(actor.DefaultAppearance)}",
                    $"Default appearance '{actor.DefaultAppearance}' is not defined."));
            }
        }
    }

    private static void ValidatePosition(
        Vector2 position,
        string property,
        ICollection<ConfigurationIssue> issues)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Actors)}.{property}",
                "Position components must be finite."));
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
