#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Core;
using Majo.Runtime.Resources;
using RuntimeResourcePath = Majo.Runtime.Resources.ResourcePath;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class MajoProjectConfiguration : Resource
{
    [Export]
    public RuntimeConfiguration? Runtime { get; set; }

    [Export]
    public OperationConfiguration? Operations { get; set; }

    [Export]
    public ResourceConfiguration? Resources { get; set; }

    [Export]
    public ActorConfiguration? Actors { get; set; }

    [Export]
    public DialogueConfiguration? Dialogue { get; set; }

    public static MajoProjectConfiguration CreateDefault() => new()
    {
        Runtime = new RuntimeConfiguration(),
        Operations = new OperationConfiguration(),
        Resources = new ResourceConfiguration(),
        Actors = new ActorConfiguration(),
        Dialogue = new DialogueConfiguration(),
    };

    public ConfigurationValidationResult Validate(Func<string, bool>? resourceExists = null)
    {
        resourceExists ??= path => ResourceLoader.Exists(path);
        var issues = new List<ConfigurationIssue>();

        ValidateRuntime(issues);
        ValidateOperations(issues);
        ValidateResources(issues);
        ValidateActors(issues, resourceExists);
        ValidateDialogue(issues);

        return new ConfigurationValidationResult(issues);
    }

    private void ValidateResources(ICollection<ConfigurationIssue> issues)
    {
        if (Resources is null)
        {
            issues.Add(new ConfigurationIssue(nameof(Resources), "Configuration is required."));
            return;
        }

        if (string.IsNullOrWhiteSpace(Resources.RootPath) ||
            !Resources.RootPath.StartsWith("res://", StringComparison.Ordinal))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Resources)}.{nameof(Resources.RootPath)}",
                "Root path must be a Godot project path starting with 'res://'."));
        }

        ValidateResourceKeyPrefix(Resources.CharactersPath, nameof(Resources.CharactersPath), issues);
        ValidateResourceKeyPrefix(Resources.BackgroundsPath, nameof(Resources.BackgroundsPath), issues);
        ValidateResourceKeyPrefix(Resources.ScriptsPath, nameof(Resources.ScriptsPath), issues);
    }

    private static void ValidateResourceKeyPrefix(
        string value,
        string property,
        ICollection<ConfigurationIssue> issues)
    {
        try
        {
            _ = new ResourceKey(value);
        }
        catch (ArgumentException exception)
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Resources)}.{property}", exception.Message));
        }
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
                (actor.FactoryConfiguration is null &&
                    (!string.IsNullOrWhiteSpace(actor.ResourcePathPrefix) ||
                        actor.Appearances.Count > 0));
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
            if (!string.IsNullOrWhiteSpace(actor.ResourcePathPrefix))
            {
                try
                {
                    _ = new ResourceKey(actor.ResourcePathPrefix);
                }
                catch (ArgumentException exception)
                {
                    issues.Add(new ConfigurationIssue(
                        $"{path}.{nameof(actor.ResourcePathPrefix)}", exception.Message));
                }
            }

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
                else
                {
                    var resolvedPath = RuntimeResourcePath.IsRooted(appearance.TexturePath)
                        ? appearance.TexturePath
                        : RuntimeResourcePath.Combine(
                            Resources?.RootPath ?? ResourceConfiguration.DefaultRootPath,
                            appearance.TexturePath);
                    if (!resourceExists(resolvedPath))
                    {
                        issues.Add(new ConfigurationIssue(
                            $"{appearancePath}.{nameof(appearance.TexturePath)}",
                            $"Resource '{resolvedPath}' does not exist."));
                    }
                }
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

        if (string.IsNullOrWhiteSpace(Dialogue.DefaultProjectId))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Dialogue)}.{nameof(Dialogue.DefaultProjectId)}",
                "Default project ID is required."));
        }

        if (string.IsNullOrWhiteSpace(Dialogue.DefaultStartNode))
        {
            issues.Add(new ConfigurationIssue(
                $"{nameof(Dialogue)}.{nameof(Dialogue.DefaultStartNode)}",
                "Default start node is required."));
        }
    }
}
