#nullable enable

using System;
using Godot;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionRenderNodes : IDisposable
{
    private readonly ShaderMaterial outputMaterial = GodotDirectionRendering.CreateOutputMaterial();
    private bool disposed;
    internal static SubViewport CreateViewport(string name) => new()
    {
        Name = name,
        Disable3D = true,
        TransparentBg = false,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        HandleInputLocally = false,
        GuiDisableInput = true,
    };

    internal TextureRect CreateTextureRect(string name) => new()
    {
        Name = name,
        Material = outputMaterial,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    internal static void SetFullRect(Control control, Vector2I size)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        control.Position = Vector2.Zero;
        control.Size = size;
    }

    internal static Vector2I ToSize(Vector2 size) => new(
        Math.Max(1, Mathf.RoundToInt(size.X)),
        Math.Max(1, Mathf.RoundToInt(size.Y)));

    internal static void QueueFree(Node node)
    {
        if (!GodotObject.IsInstanceValid(node)) return;
        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }

    internal static void AssignVisibilityLayer(Node root, uint layer)
    {
        if (root is CanvasItem item) item.VisibilityLayer = layer;
        foreach (var child in root.GetChildren()) AssignVisibilityLayer(child, layer);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        outputMaterial.Dispose();
    }
}

internal static class GodotDirectionRenderLayers
{
    internal const uint Background = 1u << 0;
    internal const uint Subject = 1u << 1;
    internal const uint BackgroundTransition = 1u << 2;
    internal const uint Stage = Background | Subject | BackgroundTransition;
}
