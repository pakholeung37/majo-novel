using System;
using System.Collections.Generic;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorConfiguration : Resource, IValidatableConfiguration
{
    [Export]
    public Vector2I ReferenceResolution { get; set; } = Vector2I.Zero;

    /// <summary>
    /// Global character baseline adjustment in scene percentages. Positive Y moves
    /// characters upward, matching Naninovel scene-position coordinates.
    /// </summary>
    [Export]
    public Vector2 ScenePositionOffset { get; set; } = Vector2.Zero;

    [Export]
    public Godot.Collections.Array<ActorDefinitionConfiguration> Actors { get; set; } = new();

    /// <summary>Project marks in scene percentages. Override legacy presets by name.</summary>
    [Export]
    public Godot.Collections.Dictionary<string, Vector2> Marks { get; set; } = new();

    public IReadOnlyDictionary<string, DirectionPoint> CreateDirectionMarks()
    {
        var marks = new Dictionary<string, DirectionPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Marks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            marks.Add(name.Trim(), new DirectionPoint(value.X, value.Y).Validate());
        }
        AddLegacy("left", LeftPosition);
        AddLegacy("center", CenterPosition);
        AddLegacy("right", RightPosition);
        return marks;

        void AddLegacy(string name, Vector2 pixels)
        {
            if (marks.ContainsKey(name)) return;
            if (ReferenceResolution.X <= 0 || ReferenceResolution.Y <= 0)
                // Projects without character rendering may omit a reference resolution.
                // Explicit percentage marks remain usable; pixel presets cannot be converted.
                return;
            marks.Add(name, new DirectionPoint(
                pixels.X / ReferenceResolution.X * 100f,
                (1f - pixels.Y / ReferenceResolution.Y) * 100f).Validate());
        }
    }

    [Export]
    public Vector2 LeftPosition { get; set; } = new(320, 540);

    [Export]
    public Vector2 CenterPosition { get; set; } = new(640, 540);

    [Export]
    public Vector2 RightPosition { get; set; } = new(960, 540);

    public void Validate(ConfigurationValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var actorIds = new HashSet<string>(StringComparer.Ordinal);
        if ((ReferenceResolution.X == 0) != (ReferenceResolution.Y == 0) ||
            ReferenceResolution.X < 0 || ReferenceResolution.Y < 0)
        {
            context.AddIssue(
                nameof(ReferenceResolution),
                "Reference resolution must be zero for dynamic sizing or positive on both axes.");
        }

        ValidatePosition(
            ScenePositionOffset,
            nameof(ScenePositionOffset),
            context);

        if (ScenePositionOffset != Vector2.Zero &&
            (ReferenceResolution.X <= 0 || ReferenceResolution.Y <= 0))
        {
            context.AddIssue(
                nameof(ScenePositionOffset),
                "A scene-position offset requires a positive reference resolution.");
        }

        ValidatePosition(LeftPosition, nameof(LeftPosition), context);
        ValidatePosition(CenterPosition, nameof(CenterPosition), context);
        ValidatePosition(RightPosition, nameof(RightPosition), context);

        for (var index = 0; index < Actors.Count; index++)
        {
            var actor = Actors[index];
            var actorSection = $"{nameof(Actors)}[{index}]";
            if (actor is null)
            {
                context.AddIssue(actorSection, "Actor definition is required.");
                continue;
            }

            actor.Validate(context.ForSection(actorSection), actorIds);
        }
    }

    private static void ValidatePosition(
        Vector2 position,
        string property,
        ConfigurationValidationContext context)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            context.AddIssue(
                property,
                "Position components must be finite.");
        }
    }
}
