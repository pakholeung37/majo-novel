#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Majo.Runtime.Direction.Core;
using Majo.Runtime.Direction.Rendering;
using static Majo.Runtime.Direction.Godot.GodotDirectionRenderNodes;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionScreenComposer : IDisposable
{
    private readonly GodotDirectionRenderNodes nodes;
    private readonly Node owner;
    private readonly Dictionary<LayoutSlotId, TextureRect> screenSlots = new();
    private readonly Dictionary<TextureRect, ShaderMaterial> slotMaterials = new();
    private readonly HashSet<GodotDirectionViewSurface> screenViews = new();
    private ScreenState? screenLayoutBefore;
    private ScreenState? screenLayoutTarget;
    private float screenLayoutProgress;
    private bool disposed;

    internal GodotDirectionScreenComposer(Node owner, GodotDirectionRenderNodes nodes)
    {
        this.owner = owner;
        this.nodes = nodes;
        ScreenViewport = CreateViewport("MajoDirectionScreenViewport");
        ScreenCanvas = new Control
        {
            Name = "ScreenCanvas",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var mainScreenSlot = nodes.CreateTextureRect("ScreenSlot-main");
        ScreenTransitionSource = nodes.CreateTextureRect("ScreenTransitionSource");
        ScreenTransitionSource.ZIndex = 0;
        ScreenTransitionSource.Visible = false;
        ScreenCanvas.AddChild(mainScreenSlot);
        ScreenViewport.AddChild(ScreenCanvas);
        screenSlots[LayoutSlotId.Main] = mainScreenSlot;

        OutputLayer = new CanvasLayer
        {
            Name = "MajoDirectionScreen",
            Layer = 5,
            Visible = false,
        };
        LiveOutput = nodes.CreateTextureRect("LiveOutput");
        Curtain = new ColorRect
        {
            Name = "DirectionCurtain",
            Color = new Color(Colors.Black, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        OutputLayer.AddChild(LiveOutput);
        OutputLayer.AddChild(ScreenTransitionSource);
        OutputLayer.AddChild(Curtain);
    }

    internal SubViewport ScreenViewport { get; }
    internal Control ScreenCanvas { get; }
    internal CanvasLayer OutputLayer { get; }
    internal TextureRect LiveOutput { get; }
    internal TextureRect ScreenTransitionSource { get; }
    internal ColorRect Curtain { get; }
    internal ScreenState PublishedScreen { get; private set; } = ScreenState.Default;
    internal StableEffectState PublishedEffects { get; private set; } = StableEffectState.Default;
    internal IReadOnlyCollection<GodotDirectionViewSurface> Views => screenViews;
    internal void BindMainView(GodotDirectionViewSurface view)
    {
        screenSlots[LayoutSlotId.Main].Texture = view.Viewport.GetTexture();
        screenViews.Add(view);
    }
    internal void RemoveView(GodotDirectionViewSurface view) => screenViews.Remove(view);
    internal void Attach()
    {
        owner.AddChild(ScreenViewport);
        owner.AddChild(OutputLayer);
        LiveOutput.Texture = ScreenViewport.GetTexture();
    }

    internal void PublishScreen(ScreenState state, Func<ViewId, GodotDirectionViewSurface> resolveView)
    {
        PublishedScreen = state ?? throw new ArgumentNullException(nameof(state));
        screenLayoutBefore = null;
        screenLayoutTarget = null;
        screenViews.Clear();
        foreach (var slot in screenSlots.Values) slot.Visible = false;
        foreach (var pair in state.Bindings)
        {
            var output = GetOrCreateScreenSlot(pair.Key);
            var view = resolveView(pair.Value);
            output.Texture = view.Viewport.GetTexture();
            output.Visible = true;
            screenViews.Add(view);
            ApplyScreenSlot(output, state, pair.Key, ScreenViewport.Size);
        }
    }

    internal void PublishScreenLayout(ScreenState state, ScreenState before, float progress, Func<ViewId, GodotDirectionViewSurface> resolveView)
    {
        PublishedScreen = state;
        screenLayoutBefore = before;
        screenLayoutTarget = state;
        screenLayoutProgress = Mathf.Clamp(progress, 0, 1);
        screenViews.Clear();
        foreach (var slot in screenSlots.Values) slot.Visible = false;
        foreach (var slot in before.Bindings.Keys.Union(state.Bindings.Keys))
        {
            if (!state.Bindings.TryGetValue(slot, out var viewId) &&
                !before.Bindings.TryGetValue(slot, out viewId))
                continue;
            var output = GetOrCreateScreenSlot(slot);
            var view = resolveView(viewId);
            output.Texture = view.Viewport.GetTexture();
            var hasTarget = state.Bindings.ContainsKey(slot);
            output.Visible = hasTarget || progress < 1;
            if (output.Visible) screenViews.Add(view);
            ApplyScreenSlot(output, state, slot, ScreenViewport.Size, before, progress);
        }
    }

    public void PublishEffects(StableEffectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Curtain.Validate();
        PublishedEffects = state;
        Curtain.Color = new Color(
            state.Curtain.Color.R,
            state.Curtain.Color.G,
            state.Curtain.Color.B,
            state.Curtain.Opacity);
        Curtain.Visible = state.Curtain.Opacity > 0;
    }

    internal void PublishScreenTransition(Texture2D texture)
    {
        ScreenTransitionSource.Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        ScreenTransitionSource.Modulate = Colors.White;
        ScreenTransitionSource.Visible = true;
    }

    internal void SetScreenTransitionOpacity(float opacity)
    {
        if (!GodotObject.IsInstanceValid(ScreenTransitionSource)) return;
        ScreenTransitionSource.Modulate = new Color(1, 1, 1, Mathf.Clamp(opacity, 0, 1));
    }

    internal void ClearScreenTransition()
    {
        if (!GodotObject.IsInstanceValid(ScreenTransitionSource)) return;
        LiveOutput.Visible = true;
        ScreenTransitionSource.Visible = false;
        ScreenTransitionSource.Texture = null;
        ScreenTransitionSource.Modulate = Colors.White;
    }

    internal void Resize(Vector2I screenSize, Vector2I outputSize, Func<ViewId, GodotDirectionViewSurface> resolveView)
    {
        ScreenViewport.Size = screenSize;
        SetFullRect(ScreenCanvas, screenSize);
        SetFullRect(LiveOutput, outputSize);
        SetFullRect(ScreenTransitionSource, outputSize);
        SetFullRect(Curtain, outputSize);
        foreach (var pair in screenSlots)
            if (pair.Value.Visible) ApplyScreenSlot(pair.Value, PublishedScreen, pair.Key, screenSize);
        if (screenLayoutBefore is not null && screenLayoutTarget is not null)
            PublishScreenLayout(screenLayoutTarget, screenLayoutBefore, screenLayoutProgress, resolveView);
    }
    internal void ResetSlots()
    {
        foreach (var slot in screenSlots.Keys.Where(slot => slot != LayoutSlotId.Main).ToArray())
        {
            ReleaseSlot(screenSlots[slot]);
            screenSlots.Remove(slot);
        }
    }
    private void ReleaseSlot(TextureRect output)
    {
        if (slotMaterials.Remove(output, out var material))
        {
            if (GodotObject.IsInstanceValid(output)) output.Material = null;
            material.Dispose();
        }
        QueueFree(output);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var slot in screenSlots.Values) ReleaseSlot(slot);
        screenSlots.Clear();
        screenViews.Clear();
        PublishedScreen = ScreenState.Default;
        PublishedEffects = StableEffectState.Default;
        QueueFree(OutputLayer);
        QueueFree(ScreenViewport);
    }
    private TextureRect GetOrCreateScreenSlot(LayoutSlotId slot)
    {
        if (screenSlots.TryGetValue(slot, out var existing)) return existing;
        var output = nodes.CreateTextureRect($"ScreenSlot-{slot.Value}");
        ScreenCanvas.AddChild(output);
        screenSlots.Add(slot, output);
        return output;
    }

    private static Rect2 SlotRect(LayoutId layout, LayoutSlotId slot, Vector2I viewportSize) =>
        DirectionLayoutCalculator.CalculateSlotRect(layout, slot, viewportSize);

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

    private void ApplyScreenSlot(TextureRect output, ScreenState state, LayoutSlotId slot,
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
            slotMaterials.Add(output, material);
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
