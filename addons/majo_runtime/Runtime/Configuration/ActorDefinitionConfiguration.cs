#nullable enable

using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Actors;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorDefinitionConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public string Id { get; set; } = string.Empty;

    [Export]
    public string FactoryId { get; set; } = ActorFactoryIds.Character;

    [Export]
    public Resource? FactoryConfiguration { get; set; }

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

        if (FactoryConfiguration is IValidatableConfiguration validatable)
            validatable.Validate(context.ForSection(nameof(FactoryConfiguration)));
    }
}
