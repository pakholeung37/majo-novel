#nullable enable

using Godot;
using Majo.Runtime.Operations;

namespace Majo.Runtime.Actors.Godot;

public sealed class BackgroundActor : ActorBase, IBackgroundActor
{
    public BackgroundActor(
        ActorDefinition definition,
        Control container,
        IOperationRunner runner,
        IActorTextureLoader textureLoader)
        : base(definition, container, runner, textureLoader)
    {
        Container.Resized += LayoutViews;
        CurrentView = CreateView();
        SetAppearance(definition.DefaultAppearance);
    }

    private Control Container => (Control)base.View;

    public TextureRect CurrentView { get; private set; }

    public string Slot { get; private set; } = BackgroundSlotIds.Main;

    public override Vector2 Position => Container.Position;

    public void SetSlot(string slot) =>
        PersistentWrite(() => Slot = BackgroundSlotIds.Normalize(slot));

    protected override void ApplyTexture(Texture2D texture)
    {
        CurrentView.Texture = texture;
        LayoutView(CurrentView);
    }

    protected override void ApplyPosition(Vector2 position)
    {
        Container.Position = position;
    }

    private TextureRect CreateView()
    {
        var view = new TextureRect
        {
            Name = "Appearance",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Container.AddChild(view);
        return view;
    }

    private void LayoutViews()
    {
        foreach (var child in Container.GetChildren())
        {
            if (child is TextureRect view)
            {
                LayoutView(view);
            }
        }
    }

    private void LayoutView(TextureRect view)
    {
        var textureSize = view.Texture?.GetSize() ?? Vector2.Zero;
        var viewportSize = Container.Size;
        if (textureSize.X <= 0 || textureSize.Y <= 0 ||
            viewportSize.X <= 0 || viewportSize.Y <= 0)
        {
            return;
        }

        // Size the view to the complete "cover" result instead of cropping the
        // texture into a viewport-sized TextureRect. Wide background and still
        // assets intentionally contain horizontal bleed for camera movement;
        // keeping the expanded rect lets a shake reveal that bleed rather than
        // the black Stage backdrop.
        var scale = Mathf.Max(
            viewportSize.X / textureSize.X,
            viewportSize.Y / textureSize.Y);
        var renderedSize = textureSize * scale;
        view.AnchorLeft = 0.5f;
        view.AnchorTop = 0.5f;
        view.AnchorRight = 0.5f;
        view.AnchorBottom = 0.5f;
        view.OffsetLeft = -renderedSize.X / 2;
        view.OffsetTop = -renderedSize.Y / 2;
        view.OffsetRight = renderedSize.X / 2;
        view.OffsetBottom = renderedSize.Y / 2;
    }

}
