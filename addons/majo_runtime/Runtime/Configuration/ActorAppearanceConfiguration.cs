#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using RuntimeResourcePath = Majo.Runtime.Resources.ResourcePath;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorAppearanceConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public string Id { get; set; } = string.Empty;

    [Export(PropertyHint.File)]
    public string TexturePath { get; set; } = string.Empty;

    public void Validate(ConfigurationValidationContext context) =>
        Validate(context, null);

    public void Validate(ConfigurationValidationContext context, HashSet<string>? appearanceIds)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(Id))
        {
            context.AddIssue(nameof(Id), "Appearance ID is required.");
        }
        else if (appearanceIds != null && !appearanceIds.Add(Id))
        {
            context.AddIssue(
                nameof(Id),
                $"Appearance ID '{Id}' is duplicated.");
        }

        if (string.IsNullOrWhiteSpace(TexturePath))
        {
            context.AddIssue(nameof(TexturePath), "Texture path is required.");
        }
        else
        {
            var rootPath = context.Root?.Resources?.RootPath ?? ResourceConfiguration.DefaultRootPath;
            var resolvedPath = RuntimeResourcePath.IsRooted(TexturePath)
                ? TexturePath
                : RuntimeResourcePath.Combine(rootPath, TexturePath);

            if (context.Root?.Resources?.FailOnMissingAssets == true &&
                !context.ResourceExists(resolvedPath))
            {
                context.AddIssue(
                    nameof(TexturePath),
                    $"Resource '{resolvedPath}' does not exist.");
            }
        }
    }
}
