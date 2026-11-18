#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class StateConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public bool Enabled { get; set; } = true;

    [Export]
    public string StorageNamespace { get; set; } = "majo/state";

    [Export]
    public string GameBuildId { get; set; } = "development";

    [Export]
    public DialogueTravelConfiguration DialogueTravel { get; set; } = new();

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(StorageNamespace) ||
            StorageNamespace.Contains("..", StringComparison.Ordinal) ||
            StorageNamespace.StartsWith('/'))
        {
            context.AddIssue(
                nameof(StorageNamespace),
                "Storage namespace must be a safe relative logical path.");
        }

        if (string.IsNullOrWhiteSpace(GameBuildId))
        {
            context.AddIssue(
                nameof(GameBuildId),
                "Game build ID is required.");
        }

        if (DialogueTravel is null)
        {
            context.AddIssue(
                nameof(DialogueTravel),
                "Configuration is required.");
            return;
        }

        DialogueTravel.Validate(context.ForSection(nameof(DialogueTravel)));
    }
}
