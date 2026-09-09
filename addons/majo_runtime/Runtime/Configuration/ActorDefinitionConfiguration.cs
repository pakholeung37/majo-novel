#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorDefinitionConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public string Id { get; set; } = string.Empty;

    [Export]
    public string FactoryId { get; set; } = ActorFactoryIds.Character;

    [Export]
    public string DefaultAppearance { get; set; } = "default";

    [Export]
    public string ResourcePathPrefix { get; set; } = string.Empty;

    [Export]
    public CharacterPositionPreset InitialPosition { get; set; } =
        CharacterPositionPreset.Center;

    [Export]
    public Resource? FactoryConfiguration { get; set; }

    [Export]
    public Godot.Collections.Array<ActorAppearanceConfiguration> Appearances { get; set; } = new();

    public void Validate(ConfigurationValidationContext context) =>
        Validate(context, null);

    public void Validate(ConfigurationValidationContext context, HashSet<string>? actorIds)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(Id))
        {
            context.AddIssue(nameof(Id), "ID is required.");
        }
        else if (actorIds != null && !actorIds.Add(Id))
        {
            context.AddIssue(
                nameof(Id),
                $"Actor ID '{Id}' is duplicated.");
        }

        if (string.IsNullOrWhiteSpace(FactoryId))
        {
            context.AddIssue(
                nameof(FactoryId),
                "Factory ID is required.");
        }

        var builtInTextureFactory = FactoryId is ActorFactoryIds.Character or ActorFactoryIds.Background;
        if (builtInTextureFactory && FactoryConfiguration is not null)
        {
            context.AddIssue(
                nameof(FactoryConfiguration),
                $"Built-in factory '{FactoryId}' uses the texture appearance fields.");
        }

        var usesTextureDefinition = builtInTextureFactory ||
            (FactoryConfiguration is null &&
                (!string.IsNullOrWhiteSpace(ResourcePathPrefix) || Appearances.Count > 0));
        if (!usesTextureDefinition)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DefaultAppearance))
        {
            context.AddIssue(
                nameof(DefaultAppearance),
                "Default appearance is required.");
        }

        var appearanceIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ResourcePathPrefix))
        {
            try
            {
                _ = new ResourceKey(ResourcePathPrefix);
            }
            catch (ArgumentException exception)
            {
                context.AddIssue(
                    nameof(ResourcePathPrefix), exception.Message);
            }
        }

        for (var appearanceIndex = 0; appearanceIndex < Appearances.Count; appearanceIndex++)
        {
            var appearance = Appearances[appearanceIndex];
            var appSection = $"{nameof(Appearances)}[{appearanceIndex}]";
            if (appearance is null)
            {
                context.AddIssue(
                    appSection, "Appearance definition is required.");
                continue;
            }

            appearance.Validate(context.ForSection(appSection), appearanceIds);
        }
    }
}
