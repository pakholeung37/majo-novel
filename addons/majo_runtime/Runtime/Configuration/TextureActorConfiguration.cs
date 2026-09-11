#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;
using Majo.Runtime.Resources;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class TextureActorConfiguration : Resource, IValidatableConfiguration
{
    [Export] public string DefaultAppearance { get; set; } = "default";
    [Export] public string ResourcePathPrefix { get; set; } = string.Empty;
    [Export] public CharacterPositionPreset InitialPosition { get; set; } = CharacterPositionPreset.Center;
    [Export] public Godot.Collections.Array<ActorAppearanceConfiguration> Appearances { get; set; } = new();

    public void Validate(ConfigurationValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(DefaultAppearance))
            context.AddIssue(nameof(DefaultAppearance), "Default appearance is required.");
        if (!string.IsNullOrWhiteSpace(ResourcePathPrefix))
            try { _ = new ResourceKey(ResourcePathPrefix); }
            catch (ArgumentException error) { context.AddIssue(nameof(ResourcePathPrefix), error.Message); }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Appearances.Count; index++)
        {
            var appearance = Appearances[index];
            var section = context.ForSection($"{nameof(Appearances)}[{index}]");
            if (appearance is null) section.AddIssue(string.Empty, "Appearance definition is required.");
            else appearance.Validate(section, ids);
        }
    }
}
