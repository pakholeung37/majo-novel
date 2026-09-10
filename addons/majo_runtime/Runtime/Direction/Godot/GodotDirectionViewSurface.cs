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
        CompositeViewport = new SubViewport
        {
            Name = "ViewCompositeViewport",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        BackgroundViewport = new SubViewport
        {
            Name = "ViewBackgroundViewport",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            CanvasCullMask = GodotDirectionRenderHost.BackgroundVisibilityLayer,
        };
        TransitionBackgroundViewport = new SubViewport
        {
            Name = "ViewTransitionBackgroundViewport",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            CanvasCullMask = GodotDirectionRenderHost.BackgroundTransitionVisibilityLayer,
        };
        ContentViewport = new SubViewport
        {
            Name = "ViewContentViewport",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            CanvasCullMask = GodotDirectionRenderHost.SubjectVisibilityLayer,
        };
        viewport.RemoveChild(canvas);
        viewport.AddChild(CompositeViewport);
        CompositeViewport.AddChild(BackgroundViewport);
        CompositeViewport.AddChild(TransitionBackgroundViewport);
        CompositeViewport.AddChild(ContentViewport);
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
        BackgroundOutput = new TextureRect
        {
            Name = "ViewBackgroundOutput",
            Texture = BackgroundViewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        TransitionBackgroundOutput = new TextureRect
        {
            Name = "ViewTransitionBackgroundOutput",
            Texture = TransitionBackgroundViewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        TransitionBlendOutput = new TextureRect
        {
            Name = "ViewTransitionBlendOutput",
            Texture = BackgroundViewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        CompositeViewport.AddChild(BackgroundOutput);
        CompositeViewport.AddChild(TransitionBackgroundOutput);
        CompositeViewport.AddChild(TransitionBlendOutput);
        CompositeViewport.AddChild(Output);
        CompositeOutput = new TextureRect
        {
            Name = "ViewCompositeOutput",
            Texture = CompositeViewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        viewport.AddChild(CompositeOutput);
        viewport.AddChild(transitionSource);
        Id = id;
        Viewport = viewport;
        Canvas = canvas;
        Transform = transform;
        Content = content;
        TransitionSource = transitionSource;
    }

    public SubViewport ContentViewport { get; }
    public SubViewport CompositeViewport { get; }
    public TextureRect CompositeOutput { get; }
    public SubViewport BackgroundViewport { get; }
    public TextureRect BackgroundOutput { get; }
    public SubViewport TransitionBackgroundViewport { get; }
    public TextureRect TransitionBackgroundOutput { get; }
    public TextureRect TransitionBlendOutput { get; }
    public TextureRect Output { get; }
    public ViewId Id { get; }
    public SubViewport Viewport { get; }
    public Control Canvas { get; }
    public Node2D Transform { get; }
    public TextureRect Content { get; }
    public TextureRect TransitionSource { get; }
    public ViewState? PublishedView { get; set; }
    public ViewPose CurrentPose { get; set; } = ViewPose.Default;
    public ViewParallaxState CurrentParallax { get; set; } = ViewParallaxState.Off;
    public ViewIdleState CurrentIdle { get; set; } = ViewIdleState.Off;
    public CameraPoseDelta ModifierDelta { get; set; } = CameraPoseDelta.Identity;
}
