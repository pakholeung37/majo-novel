#nullable enable

using System;
using Godot;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed partial class GodotDirectionRenderHost
{
    // Slots sample independently positioned full camera images. Both halves use the same
    // screen-space line; camera rotation and zoom never enter the mask calculation.
    private const string ScreenSlotShaderPath =
        "res://addons/majo_runtime/Runtime/Direction/Shaders/ScreenSlot.gdshader";

    private readonly record struct SlotPlacement(Vector2 Anchor, Vector2 SampleSize,
        Vector4 Clip, float Divider, float Angle);

    private static SlotPlacement GetSlotPlacement(ScreenState state, LayoutSlotId slot,
        Vector2I size, Vector2 textureSize)
    {
        var screen = new Vector2(size.X, size.Y);
        var right = slot.Value == "inset";
        var split = state.Layout.Value.Equals("split", StringComparison.OrdinalIgnoreCase);
        var rect = split ? new Rect2(Vector2.Zero, screen) : SlotRect(state.Layout, slot, size);
        var settings = state.Split ?? new SplitLayout();
        var point = right ? settings.InsetAt : settings.MainAt;
        var anchor = split
            ? point is { } at ? new Vector2(at.X / 100, 1 - at.Y / 100)
                : new Vector2(right ? (1 + settings.Divider / 100) / 2 : settings.Divider / 200, .5f)
            : (rect.Position + rect.Size / 2) / screen;
        var cover = Mathf.Max(rect.Size.X / textureSize.X, rect.Size.Y / textureSize.Y);
        var sample = textureSize * cover / screen;
        var clip = new Vector4(rect.Position.X / screen.X, rect.Position.Y / screen.Y,
            rect.End.X / screen.X, rect.End.Y / screen.Y);
        var single = state.Layout.Value.Equals("single", StringComparison.OrdinalIgnoreCase);
        if (!state.Bindings.ContainsKey(slot) && !single) clip.Z = clip.X;
        return new SlotPlacement(anchor, sample, clip,
            split ? settings.Divider / 100 : single ? 1 : right ? 0 : 1,
            split ? Mathf.DegToRad(settings.Angle) : 0);
    }

    private static void ApplyScreenSlot(TextureRect output, ScreenState state, LayoutSlotId slot,
        Vector2I size, ScreenState? before = null, float progress = 1)
    {
        var textureSize = output.Texture?.GetSize() ?? new Vector2(size.X, size.Y);
        var target = GetSlotPlacement(state, slot, size, textureSize);
        var from = before is null ? target : GetSlotPlacement(before, slot, size, textureSize);
        var material = output.Material as ShaderMaterial;
        if (material?.Shader?.ResourcePath != ScreenSlotShaderPath)
        {
            material = new ShaderMaterial
            {
                Shader = ResourceLoader.Load<Shader>(ScreenSlotShaderPath) ??
                    throw new InvalidOperationException(
                        $"Screen slot shader could not be loaded: '{ScreenSlotShaderPath}'."),
            };
            output.Material = material;
        }
        SetFullRect(output, size);
        // Inset must blend over main regardless of binding insertion order.
        output.ZIndex = slot.Value == "inset" ? 1 : 0;
        output.StretchMode = TextureRect.StretchModeEnum.Scale;
        material.SetShaderParameter("screen_size", new Vector2(size.X, size.Y));
        material.SetShaderParameter("anchor", from.Anchor.Lerp(target.Anchor, progress));
        material.SetShaderParameter("sample_size", from.SampleSize.Lerp(target.SampleSize, progress));
        material.SetShaderParameter("clip_rect", from.Clip.Lerp(target.Clip, progress));
        material.SetShaderParameter("divider", Mathf.Lerp(from.Divider, target.Divider, progress));
        material.SetShaderParameter("angle", Mathf.Lerp(from.Angle, target.Angle, progress));
        material.SetShaderParameter("right_side", slot.Value == "inset");
    }
}
