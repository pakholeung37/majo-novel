#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Actors.Godot;

/// <summary>Both mounting domains are available in every factory environment.</summary>
public sealed class ActorMountRoots
{
    public ActorMountRoots(Node2D characterRoot, Control backgroundRoot)
    {
        ArgumentNullException.ThrowIfNull(characterRoot);
        ArgumentNullException.ThrowIfNull(backgroundRoot);
        if (!GodotObject.IsInstanceValid(characterRoot) || !GodotObject.IsInstanceValid(backgroundRoot))
            throw new ArgumentException("Actor mounting roots must be valid nodes.");
        CharacterRoot = characterRoot;
        BackgroundRoot = backgroundRoot;
    }

    public Node2D CharacterRoot { get; }
    public Control BackgroundRoot { get; }

    internal static ActorMountRoots CreateUnder(Node2D root, Vector2 size)
    {
        var backgrounds = new Control
        {
            Name = "BackgroundFactoryRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Size = size,
        };
        root.AddChild(backgrounds);
        return new ActorMountRoots(root, backgrounds);
    }
}
