#nullable enable

using Godot;
using Majo.Runtime.Direction.Camera;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionViewSurface
{
    public GodotDirectionViewSurface(
        ViewId id,
        SubViewport viewport,
        Control canvas,
        Node2D transform,
        TextureRect content,
        TextureRect transitionSource)
    {
        ContentViewport = new SubViewport
        {
            Name = "ViewContentViewport",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        viewport.RemoveChild(canvas);
        viewport.AddChild(ContentViewport);
        ContentViewport.AddChild(canvas);
        canvas.RemoveChild(transitionSource);
        Output = new TextureRect
        {
            Name = "ViewOutput",
            Texture = ContentViewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        viewport.AddChild(Output);
        viewport.AddChild(transitionSource);
        Id = id;
        Viewport = viewport;
        Canvas = canvas;
        Transform = transform;
        Content = content;
        TransitionSource = transitionSource;
    }

    public SubViewport ContentViewport { get; }
    public TextureRect Output { get; }
    public ViewId Id { get; }
    public SubViewport Viewport { get; }
    public Control Canvas { get; }
    public Node2D Transform { get; }
    public TextureRect Content { get; }
    public TextureRect TransitionSource { get; }
    public ViewState? PublishedView { get; set; }
    public ViewPose CurrentPose { get; set; } = ViewPose.Default;
    public CameraPoseDelta ModifierDelta { get; set; } = CameraPoseDelta.Identity;
}
