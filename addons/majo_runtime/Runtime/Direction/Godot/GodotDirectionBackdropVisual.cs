#nullable enable

using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;

namespace Majo.Runtime.Direction.Godot;

/// <summary>Host clipping and layout of a generic content output or frozen snapshot.</summary>
internal sealed partial class GodotDirectionBackdropVisual : Control
{
    private readonly Node2D placement = new();
    private readonly Sprite2D output = new() { Centered = false };
    private readonly Shader shader = new() { Code = """
        shader_type canvas_item;
        render_mode unshaded;
        varying vec4 modulation;
        void vertex() { modulation = COLOR; }
        void fragment() {
            vec4 c = texture(TEXTURE, UV);
            COLOR = c.a > 0.00001 ? vec4(c.rgb / c.a, c.a) * modulation : vec4(0.0);
        }
        """ };
    private readonly ShaderMaterial material;
    public GodotBackgroundRaster? Raster { get; private set; }
    public Rect2 VisibleBounds { get; private set; }

    public GodotDirectionBackdropVisual()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        material = new ShaderMaterial { Shader = shader };
        output.Material = material;
        AddChild(placement);
        placement.AddChild(output);
        TreeExiting += () => { material.Dispose(); shader.Dispose(); };
    }

    public void Set(GodotBackgroundRaster? raster, BackdropState? state, Vector2 size)
    {
        Raster = raster;
        output.Texture = raster?.Texture;
        Visible = raster is not null && state is not null;
        Modulate = state is null ? Colors.White : DirectionLayoutCalculator.ToGodotColor(state.Tint);
        Size = size;
        Position = Vector2.Zero;
        ClipContents = state?.Placement is null;
        if (raster is null || state is null) return;
        var layout = DirectionLayoutCalculator.CalculateBackgroundLayout(size, raster.Bounds, state.Fit, state.Placement);
        placement.Transform = layout.Transform;
        output.Position = raster.Bounds.Position;
        output.Scale = raster.Bounds.Size / raster.Texture.GetSize();
        VisibleBounds = layout.VisibleBounds;
        GodotDirectionRenderHost.AssignVisibilityLayer(this, VisibilityLayer);
    }
}
