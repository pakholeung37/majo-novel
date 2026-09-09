#nullable enable

using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionStageSurface
{
    public GodotDirectionStageSurface(
        StageId id,
        SubViewport viewport,
        Control canvas,
        ColorRect background,
        TextureRect backdrop,
        TextureRect backdropTransition,
        Node2D actors)
    {
        Id = id;
        Viewport = viewport;
        Canvas = canvas;
        Background = background;
        Backdrop = backdrop;
        BackdropTransition = backdropTransition;
        Actors = actors;
        BackdropFxRoot = new Node2D { Name = "BackdropFx" };
        canvas.AddChild(BackdropFxRoot);
        canvas.MoveChild(BackdropFxRoot, 1);
        backdrop.Reparent(BackdropFxRoot, false);
        backdropTransition.Reparent(BackdropFxRoot, false);
        ModifierTarget = new GodotActorModifierTarget(BackdropFxRoot, () => new Vector2(viewport.Size.X, viewport.Size.Y) / 2);
    }

    public StageId Id { get; }
    public SubViewport Viewport { get; }
    public Control Canvas { get; }
    public ColorRect Background { get; }
    public TextureRect Backdrop { get; }
    public TextureRect BackdropTransition { get; }
    public Node2D Actors { get; }
    public Node2D BackdropFxRoot { get; }
    public GodotActorModifierTarget ModifierTarget { get; }
    public BackdropState? PublishedBackdrop { get; set; }
    public BackdropState? TransitionBackdrop { get; set; }
}
