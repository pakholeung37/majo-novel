using Godot;
using System;
using System.Collections.Generic;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Configuration;

[GlobalClass]
public partial class ActorConfiguration : Resource
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
}
