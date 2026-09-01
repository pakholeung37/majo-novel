#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Majo.Runtime.Core;
using Majo.Runtime.Direction.Core;

namespace Majo.Runtime.Direction.Godot;

internal sealed class GodotDirectionRenderHost : IRuntimeService
{
    private static readonly Vector2I FallbackResolution = new(1280, 720);
    private readonly Node owner;
    private readonly Vector2I configuredResolution;
    private readonly int maxStages;
    private readonly Dictionary<StageId, GodotDirectionStageSurface> stages = new();
    private BackdropState? transitionBackdrop;
    private Viewport? ownerViewport;
    private bool isInitialized;

    public GodotDirectionRenderHost(
        Node owner,
        Vector2I referenceResolution,
        int maxStages = 8)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        configuredResolution = referenceResolution;
        if (maxStages <= 0) throw new ArgumentOutOfRangeException(nameof(maxStages));
        this.maxStages = maxStages;

        StageViewport = CreateViewport("MajoDirectionStageViewport");
        StageCanvas = new Control
        {
            Name = "StageCanvas",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        StageBackground = new ColorRect
        {
            Name = "StageBackground",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Backdrop = CreateTextureRect("Backdrop");
        BackdropTransition = CreateTextureRect("BackdropTransition");
        BackdropTransition.Visible = false;
        Actors = new Node2D { Name = "Actors" };
        StageCanvas.AddChild(StageBackground);
        StageCanvas.AddChild(Backdrop);
        StageCanvas.AddChild(BackdropTransition);
        StageCanvas.AddChild(Actors);
        StageViewport.AddChild(StageCanvas);
        stages.Add(StageId.Main, new GodotDirectionStageSurface(
            StageId.Main,
            StageViewport,
            StageCanvas,
            StageBackground,
            Backdrop,
            BackdropTransition,
            Actors));

        ViewViewport = CreateViewport("MajoDirectionViewViewport");
        ViewCanvas = new Control
        {
            Name = "ViewCanvas",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        ViewTransform = new Node2D { Name = "ViewTransform" };
        ViewContent = CreateTextureRect("ViewContent");
        ViewTransform.AddChild(ViewContent);
        ViewCanvas.AddChild(ViewTransform);
        ViewViewport.AddChild(ViewCanvas);

        OutputLayer = new CanvasLayer
        {
            Name = "MajoDirectionScreen",
            Layer = 5,
            Visible = false,
        };
        LiveOutput = CreateTextureRect("LiveOutput");
        TransitionSource = CreateTextureRect("TransitionSource");
        TransitionSource.Visible = false;
        Curtain = new ColorRect
        {
            Name = "DirectionCurtain",
            Color = new Color(Colors.Black, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        OutputLayer.AddChild(LiveOutput);
        OutputLayer.AddChild(TransitionSource);
        OutputLayer.AddChild(Curtain);
    }

    public SubViewport StageViewport { get; }
    public Control StageCanvas { get; }
    public ColorRect StageBackground { get; }
    public TextureRect Backdrop { get; }
    public TextureRect BackdropTransition { get; }
    public Node2D Actors { get; }
    public SubViewport ViewViewport { get; }
    public Control ViewCanvas { get; }
    public Node2D ViewTransform { get; }
    public TextureRect ViewContent { get; }
    public CanvasLayer OutputLayer { get; }
    public TextureRect LiveOutput { get; }
    public TextureRect TransitionSource { get; }
    public ColorRect Curtain { get; }
    public BackdropState? PublishedBackdrop { get; private set; }
    public ViewState? PublishedView { get; private set; }
    public ScreenState PublishedScreen { get; private set; } = ScreenState.Default;
    public StableEffectState PublishedEffects { get; private set; } = StableEffectState.Default;

    public IReadOnlyCollection<StageId> StageIds => stages.Keys;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isInitialized) return Task.CompletedTask;

        owner.AddChild(StageViewport);
        owner.AddChild(ViewViewport);
        owner.AddChild(OutputLayer);
        Resize();
        ViewContent.Texture = StageViewport.GetTexture();
        LiveOutput.Texture = ViewViewport.GetTexture();
        isInitialized = true;
        PublishedView = new ViewState(
            ViewId.Main,
            new StageViewSource(StageId.Main),
            ViewPose.Default);
        ApplyViewPose(ViewPose.Default);
        PublishEffects(StableEffectState.Default);
        ownerViewport = owner.GetViewport();
        ownerViewport.SizeChanged += Resize;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        foreach (var surface in stages.Values.ToArray())
        {
            surface.Backdrop.Texture = null;
            surface.Backdrop.Modulate = Colors.White;
            surface.Backdrop.Visible = false;
            ClearBackdropTransition(surface.Id);
            surface.PublishedBackdrop = null;
            if (surface.Id != StageId.Main)
            {
                stages.Remove(surface.Id);
                surface.Viewport.QueueFree();
            }
        }
        ViewContent.Texture = StageViewport.GetTexture();
        ViewContent.Modulate = Colors.White;
        ClearViewTransition();
        PublishedBackdrop = null;
        PublishedView = new ViewState(
            ViewId.Main,
            new StageViewSource(StageId.Main),
            ViewPose.Default);
        ApplyViewPose(ViewPose.Default);
        PublishedScreen = ScreenState.Default;
        PublishEffects(StableEffectState.Default);
        Deactivate();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isInitialized) return Task.CompletedTask;
        if (ownerViewport is not null && GodotObject.IsInstanceValid(ownerViewport))
            ownerViewport.SizeChanged -= Resize;
        ClearBackdropTransition();
        ClearViewTransition();
        if (GodotObject.IsInstanceValid(OutputLayer)) OutputLayer.QueueFree();
        if (GodotObject.IsInstanceValid(ViewViewport)) ViewViewport.QueueFree();
        foreach (var surface in stages.Values)
        {
            if (GodotObject.IsInstanceValid(surface.Viewport)) surface.Viewport.QueueFree();
        }
        stages.Clear();
        PublishedBackdrop = null;
        PublishedView = null;
        PublishedScreen = ScreenState.Default;
        PublishedEffects = StableEffectState.Default;
        ownerViewport = null;
        isInitialized = false;
        return Task.CompletedTask;
    }

    public void PublishBackdrop(BackdropState? state, Texture2D? texture)
        => PublishBackdrop(StageId.Main, state, texture);

    public void PublishBackdrop(StageId stageId, BackdropState? state, Texture2D? texture)
    {
        EnsureInitialized();
        Activate();
        var surface = GetStage(stageId);
        surface.PublishedBackdrop = state;
        if (stageId == StageId.Main) PublishedBackdrop = state;
        surface.Backdrop.Texture = texture;
        surface.Backdrop.Modulate = state is null
            ? Colors.White
            : ToGodot(state.Tint);
        surface.Backdrop.Visible = state is not null && texture is not null;
        if (state is not null && texture is not null)
            LayoutBackdrop(surface, surface.Backdrop, state.Fit);
    }

    public void PublishBackdropTransition(BackdropState state, Texture2D texture)
        => PublishBackdropTransition(StageId.Main, state, texture);

    public void PublishBackdropTransition(
        StageId stageId,
        BackdropState state,
        Texture2D texture)
    {
        EnsureInitialized();
        var surface = GetStage(stageId);
        surface.TransitionBackdrop = state ?? throw new ArgumentNullException(nameof(state));
        if (stageId == StageId.Main) transitionBackdrop = state;
        surface.BackdropTransition.Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        surface.BackdropTransition.Modulate = ToGodot(state.Tint);
        surface.BackdropTransition.Visible = true;
        LayoutBackdrop(surface, surface.BackdropTransition, state.Fit);
    }

    public void PublishView(ViewState state, Texture2D texture)
    {
        EnsureInitialized();
        Activate();
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(texture);
        PublishedView = state;
        ViewContent.Texture = texture;
        ViewContent.Modulate = Colors.White;
        ApplyViewPose(state.Pose);
    }

    public void PublishScreen(ScreenState state)
    {
        EnsureInitialized();
        Activate();
        PublishedScreen = state ?? throw new ArgumentNullException(nameof(state));
    }

    public void PublishEffects(StableEffectState state)
    {
        EnsureInitialized();
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

    public Tween CreateTween()
    {
        EnsureInitialized();
        return owner.CreateTween();
    }

    internal void ActivateOutput()
    {
        EnsureInitialized();
        Activate();
    }

    public ImageTexture CaptureView()
    {
        EnsureInitialized();
        Image image;
        if (!string.Equals(
            DisplayServer.GetName(),
            "headless",
            StringComparison.OrdinalIgnoreCase))
        {
            image = ViewViewport.GetTexture().GetImage();
            if (image is null || image.IsEmpty())
                throw new DirectionException("Direction View capture returned an empty image.");
        }
        else
        {
            var size = ViewViewport.Size;
            image = Image.CreateEmpty(
                Math.Max(size.X, 1),
                Math.Max(size.Y, 1),
                false,
                Image.Format.Rgba8);
            image.Fill(Colors.Black);
        }

        return ImageTexture.CreateFromImage(image);
    }

    public void ApplyViewPose(ViewPose pose)
    {
        EnsureInitialized();
        pose.Validate();
        var size = new Vector2(ViewViewport.Size.X, ViewViewport.Size.Y);
        var basis = new Transform2D(
            Mathf.DegToRad(pose.RotationDegrees),
            new Vector2(pose.Zoom, pose.Zoom),
            0,
            Vector2.Zero);
        var sceneCenter = new Vector2(
            size.X * pose.Center.X / 100,
            size.Y * (1 - pose.Center.Y / 100));
        ViewTransform.Transform = new Transform2D(
            basis.X,
            basis.Y,
            size / 2 - basis * sceneCenter);
    }

    public void ClearBackdropTransition()
        => ClearBackdropTransition(StageId.Main);

    public void ClearBackdropTransition(StageId stageId)
    {
        if (!stages.TryGetValue(stageId, out var surface) ||
            !GodotObject.IsInstanceValid(surface.BackdropTransition)) return;
        surface.BackdropTransition.Visible = false;
        surface.BackdropTransition.Texture = null;
        surface.BackdropTransition.Modulate = Colors.White;
        surface.TransitionBackdrop = null;
        if (stageId == StageId.Main) transitionBackdrop = null;
    }

    public void ClearViewTransition()
    {
        if (!GodotObject.IsInstanceValid(TransitionSource)) return;
        TransitionSource.Visible = false;
        TransitionSource.Texture = null;
        TransitionSource.Modulate = Colors.White;
    }

    private void Resize()
    {
        var visibleSize = owner.GetViewport().GetVisibleRect().Size;
        var resolution = configuredResolution.X > 0 && configuredResolution.Y > 0
            ? configuredResolution
            : visibleSize.X > 0 && visibleSize.Y > 0
                ? new Vector2I(
                    Math.Max(1, Mathf.RoundToInt(visibleSize.X)),
                    Math.Max(1, Mathf.RoundToInt(visibleSize.Y)))
                : FallbackResolution;
        foreach (var surface in stages.Values) ResizeStage(surface, resolution);
        ViewViewport.Size = resolution;
        SetFullRect(ViewCanvas, resolution);
        SetFullRect(ViewContent, resolution);
        LiveOutput.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        TransitionSource.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Curtain.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        if (PublishedView is not null) ApplyViewPose(PublishedView.Pose);
    }

    private void EnsureInitialized()
    {
        if (!isInitialized)
            throw new DirectionException("Godot Direction render host is not initialized.");
    }

    private void Activate()
    {
        foreach (var surface in stages.Values)
            surface.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        ViewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        OutputLayer.Visible = true;
    }

    private void Deactivate()
    {
        OutputLayer.Visible = false;
        foreach (var surface in stages.Values)
            surface.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        ViewViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    private static SubViewport CreateViewport(string name) => new()
    {
        Name = name,
        Disable3D = true,
        TransparentBg = false,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        HandleInputLocally = false,
        GuiDisableInput = true,
    };

    private static TextureRect CreateTextureRect(string name) => new()
    {
        Name = name,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.Scale,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static void SetFullRect(Control control, Vector2I size)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        control.Position = Vector2.Zero;
        control.Size = size;
    }

    internal GodotDirectionStageSurface GetOrCreateStage(StageId stageId)
    {
        EnsureInitialized();
        if (stages.TryGetValue(stageId, out var existing)) return existing;
        if (stages.Count >= maxStages)
        {
            throw new DirectionException(
                $"Direction Stage limit of {maxStages} was exceeded while creating '{stageId}'.");
        }

        var viewport = CreateViewport($"MajoDirectionStageViewport-{stages.Count}");
        var canvas = new Control
        {
            Name = "StageCanvas",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var background = new ColorRect
        {
            Name = "StageBackground",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var backdrop = CreateTextureRect("Backdrop");
        var transition = CreateTextureRect("BackdropTransition");
        transition.Visible = false;
        var actors = new Node2D { Name = "Actors" };
        canvas.AddChild(background);
        canvas.AddChild(backdrop);
        canvas.AddChild(transition);
        canvas.AddChild(actors);
        viewport.AddChild(canvas);
        var surface = new GodotDirectionStageSurface(
            stageId, viewport, canvas, background, backdrop, transition, actors);
        stages.Add(stageId, surface);
        owner.AddChild(viewport);
        ResizeStage(surface, StageViewport.Size);
        if (OutputLayer.Visible)
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        return surface;
    }

    internal bool HasStage(StageId stageId) => stages.ContainsKey(stageId);

    internal void ReleasePreparedStage(StageId stageId)
    {
        if (stageId == StageId.Main || !stages.Remove(stageId, out var surface)) return;
        if (GodotObject.IsInstanceValid(surface.Viewport))
        {
            surface.Viewport.GetParent()?.RemoveChild(surface.Viewport);
            surface.Viewport.QueueFree();
        }
    }

    internal GodotDirectionStageSurface GetStage(StageId stageId) =>
        stages.TryGetValue(stageId, out var surface)
            ? surface
            : throw new DirectionReferenceException(
                $"Direction Stage '{stageId}' has no prepared render surface.");

    internal BackdropState? GetPublishedBackdrop(StageId stageId) =>
        GetStage(stageId).PublishedBackdrop;

    internal Texture2D GetStageTexture(StageId stageId) => GetStage(stageId).Viewport.GetTexture();

    private void ResizeStage(GodotDirectionStageSurface surface, Vector2I resolution)
    {
        surface.Viewport.Size = resolution;
        SetFullRect(surface.Canvas, resolution);
        SetFullRect(surface.Background, resolution);
        if (surface.PublishedBackdrop is not null && surface.Backdrop.Texture is not null)
            LayoutBackdrop(surface, surface.Backdrop, surface.PublishedBackdrop.Fit);
        else
            SetFullRect(surface.Backdrop, resolution);
        if (surface.TransitionBackdrop is not null && surface.BackdropTransition.Texture is not null)
            LayoutBackdrop(surface, surface.BackdropTransition, surface.TransitionBackdrop.Fit);
        else
            SetFullRect(surface.BackdropTransition, resolution);
    }

    private static void LayoutBackdrop(
        GodotDirectionStageSurface surface,
        TextureRect view,
        BackdropFit fit)
    {
        var textureSize = view.Texture?.GetSize() ?? Vector2.Zero;
        var viewportSize = new Vector2(surface.Viewport.Size.X, surface.Viewport.Size.Y);
        if (textureSize.X <= 0 || textureSize.Y <= 0 ||
            viewportSize.X <= 0 || viewportSize.Y <= 0 ||
            fit == BackdropFit.Stretch)
        {
            SetFullRect(view, surface.Viewport.Size);
            return;
        }

        var scale = fit == BackdropFit.Cover
            ? Mathf.Max(viewportSize.X / textureSize.X, viewportSize.Y / textureSize.Y)
            : Mathf.Min(viewportSize.X / textureSize.X, viewportSize.Y / textureSize.Y);
        var renderedSize = textureSize * scale;
        view.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        view.Position = (viewportSize - renderedSize) / 2;
        view.Size = renderedSize;
    }

    private static Color ToGodot(DirectionColor color) =>
        new(color.R, color.G, color.B, color.A);
}

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
    }

    public StageId Id { get; }
    public SubViewport Viewport { get; }
    public Control Canvas { get; }
    public ColorRect Background { get; }
    public TextureRect Backdrop { get; }
    public TextureRect BackdropTransition { get; }
    public Node2D Actors { get; }
    public BackdropState? PublishedBackdrop { get; set; }
    public BackdropState? TransitionBackdrop { get; set; }
}
